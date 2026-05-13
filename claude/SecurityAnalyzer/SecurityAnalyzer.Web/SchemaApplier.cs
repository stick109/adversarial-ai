using System.Text;
using Microsoft.Data.SqlClient;

namespace SecurityAnalyzer.Web;

// Apply db/001_schema.sql at app startup so containerised deployments
// (Railway, fresh docker compose) don't need an external sqlcmd step.
//
// The schema script is idempotent: re-running it is a no-op once the
// DB, tables, columns, and seed rows already exist.  This class:
//   1. Waits for the SQL Server to accept connections (DB container
//      may still be booting when the Web container starts), then -- if
//      the user DB is set in the connection string -- waits for it to
//      accept "USE <db>" without error 904.  Without this, when an
//      existing populated volume is rebound, batch 2 of the schema
//      ("USE SecurityAnalyzer") races server-startup and fails.
//   2. Connects to `master` so the very first batch -- CREATE DATABASE
//      SecurityAnalyzer -- can run before SecurityAnalyzer exists.  The script's
//      USE SecurityAnalyzer; switches the same connection over for the rest
//      of the batches.
//   3. Splits the script on `GO` lines (Microsoft.Data.SqlClient does
//      not understand the sqlcmd batch separator) and runs each batch
//      via a single non-pooled connection.
public static class SchemaApplier
{
    public static void Apply(string connectionString, string schemaPath, ILogger? logger = null, int waitSeconds = 90)
    {
        if (!File.Exists(schemaPath))
        {
            throw new FileNotFoundException($"Schema file not found: {schemaPath}", schemaPath);
        }

        // Connect to master initially so CREATE DATABASE has somewhere to live.
        // Pooling=false because the schema connection is a one-shot at startup
        // and we don't want it cached across the connection pool.
        var masterConnStr = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = "master",
            Pooling = false,
        }.ConnectionString;

        WaitForServer(masterConnStr, logger, waitSeconds);

        // If the user DB already exists on a populated volume, SQL Server may
        // still be in the startup phase that gates USE statements -- schema
        // batch 2 (USE SecurityAnalyzer) then fails with error 904 "Database cannot
        // be autostarted during server shutdown or startup".  Probe USE on a
        // throwaway connection until it works (or 911/4060 tells us the DB
        // does not exist yet, in which case batch 1 will create it).
        var userDb = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        if (!string.IsNullOrWhiteSpace(userDb)
            && !string.Equals(userDb, "master", StringComparison.OrdinalIgnoreCase))
        {
            WaitForUserDatabaseOnline(masterConnStr, userDb, logger, waitSeconds);
        }

        var batches = SplitOnGo(File.ReadAllText(schemaPath)).ToList();
        logger?.LogInformation("Applying {Count} schema batches from {Path}", batches.Count, schemaPath);

        using var db = new SqlConnection(masterConnStr);
        db.Open();
        for (var i = 0; i < batches.Count; i++)
        {
            try
            {
                using var cmd = new SqlCommand(batches[i], db) { CommandTimeout = 60 };
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Schema batch {i + 1}/{batches.Count} failed: {ex.Message}", ex);
            }
        }
        logger?.LogInformation("Schema applied successfully");
    }

    private static void WaitForServer(string connStr, ILogger? logger, int maxSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(maxSeconds);
        Exception? last = null;
        var attempt = 0;
        while (DateTime.UtcNow < deadline)
        {
            attempt++;
            try
            {
                using var db = new SqlConnection(connStr);
                db.Open();
                using var cmd = new SqlCommand("SELECT 1", db);
                cmd.ExecuteScalar();
                logger?.LogInformation("SQL Server reachable after {Attempts} attempt(s)", attempt);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                if (attempt == 1 || attempt % 5 == 0)
                {
                    logger?.LogInformation("Waiting for SQL Server (attempt {Attempt}): {Message}", attempt, ex.Message);
                }
                Thread.Sleep(2000);
            }
        }
        throw new InvalidOperationException(
            $"SQL Server did not accept connections within {maxSeconds}s. Last error: {last?.Message}");
    }

    // Probe the user DB by actually running `USE <db>` (the operation that
    // batch 2 of the schema script performs).  This is stricter than reading
    // sys.databases.state_desc -- empirically a DB can be reported ONLINE
    // while the server-startup phase that gates USE statements is still
    // active, in which case USE throws error 904.
    //
    // Error numbers we recognise on the probe:
    //   911 -- database does not exist (first install, fine; schema batch 1
    //          will CREATE it).
    //   904 -- database cannot be autostarted during server shutdown or
    //          startup (still recovering; keep waiting).
    //   anything else -- log and keep waiting until the deadline.
    private static void WaitForUserDatabaseOnline(string masterConnStr, string dbName, ILogger? logger, int maxSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(maxSeconds);
        Exception? lastError = null;
        var attempt = 0;
        // Quote dbName as a SQL identifier; USE does not accept parameters.
        var useSql = $"USE {QuoteIdent(dbName)}; SELECT 1;";
        while (DateTime.UtcNow < deadline)
        {
            attempt++;
            try
            {
                using var db = new SqlConnection(masterConnStr);
                db.Open();
                using var cmd = new SqlCommand(useSql, db);
                cmd.ExecuteScalar();
                logger?.LogInformation(
                    "Database {Db} accepts USE after {Attempts} attempt(s)", dbName, attempt);
                return;
            }
            catch (SqlException sx) when (sx.Number == 911 || sx.Number == 4060)
            {
                logger?.LogInformation(
                    "Database {Db} does not exist yet; schema will create it", dbName);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt == 1 || attempt % 5 == 0)
                {
                    logger?.LogInformation(
                        "Waiting for database {Db} to accept USE (attempt {Attempt}): {Message}",
                        dbName, attempt, ex.Message);
                }
            }
            Thread.Sleep(2000);
        }
        throw new InvalidOperationException(
            $"Database {dbName} did not accept USE within {maxSeconds}s. " +
            $"Last error: {lastError?.Message}");
    }

    // SQL Server identifier quoting: wrap in [] and double any embedded ].
    private static string QuoteIdent(string name) =>
        "[" + name.Replace("]", "]]") + "]";

    private static IEnumerable<string> SplitOnGo(string sql)
    {
        var current = new StringBuilder();
        foreach (var rawLine in sql.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.Equals(line.Trim(), "GO", StringComparison.OrdinalIgnoreCase))
            {
                var batch = current.ToString().Trim();
                if (batch.Length > 0) yield return batch;
                current.Clear();
            }
            else
            {
                current.AppendLine(line);
            }
        }
        var lastBatch = current.ToString().Trim();
        if (lastBatch.Length > 0) yield return lastBatch;
    }
}
