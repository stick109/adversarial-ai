using System.Text;
using Microsoft.Data.SqlClient;

namespace SecurityAnalyzer.Web;

// Apply the db/ schema folder at app startup so containerised deployments
// (Railway, fresh docker compose) don't need an external sqlcmd step.
//
// The schema is a folder of NNN-name.sql files; we apply them in
// filename order on a single non-pooled connection.  Each file is one
// SQL batch (no GO separator inside), but for robustness we still
// split on GO so a multi-batch file would also work.  The whole set
// is idempotent: re-running is a no-op once the DB, tables, columns,
// and seed rows already exist.  This class:
//   1. Waits for the SQL Server to accept connections (DB container
//      may still be booting when the Web container starts), then -- if
//      the user DB is set in the connection string -- waits for it to
//      accept "USE <db>" without error 904.  Without this, when an
//      existing populated volume is rebound, the early USE batch races
//      server-startup and fails.
//   2. Connects to `master` so the first file -- CREATE DATABASE
//      SecurityAnalyzer -- can run before SecurityAnalyzer exists.  The
//      USE SecurityAnalyzer file switches the same connection over for
//      the rest of the files.
//   3. Microsoft.Data.SqlClient does not understand the sqlcmd `GO`
//      separator, so any GO lines inside a file are handled here.
public static class SchemaApplier
{
    public static void Apply(string connectionString, string schemaDir, ILogger? logger = null, int waitSeconds = 90)
    {
        if (!Directory.Exists(schemaDir))
        {
            throw new DirectoryNotFoundException($"Schema directory not found: {schemaDir}");
        }

        var sqlFiles = Directory.GetFiles(schemaDir, "*.sql")
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sqlFiles.Count == 0)
        {
            throw new InvalidOperationException($"No .sql files found in schema directory: {schemaDir}");
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
        // still be in the startup phase that gates USE statements -- the early
        // USE SecurityAnalyzer file then fails with error 904 "Database cannot
        // be autostarted during server shutdown or startup".  Probe USE on a
        // throwaway connection until it works (or 911/4060 tells us the DB
        // does not exist yet, in which case the create-db file will create it).
        var userDb = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        if (!string.IsNullOrWhiteSpace(userDb)
            && !string.Equals(userDb, "master", StringComparison.OrdinalIgnoreCase))
        {
            WaitForUserDatabaseOnline(masterConnStr, userDb, logger, waitSeconds);
        }

        logger?.LogInformation("Applying {Count} schema file(s) from {Dir}", sqlFiles.Count, schemaDir);

        using var db = new SqlConnection(masterConnStr);
        db.Open();
        foreach (var file in sqlFiles)
        {
            var name = Path.GetFileName(file);
            var batches = SplitOnGo(File.ReadAllText(file)).ToList();
            if (batches.Count == 0)
            {
                logger?.LogInformation("Skipping empty schema file {File}", name);
                continue;
            }
            for (var i = 0; i < batches.Count; i++)
            {
                try
                {
                    using var cmd = new SqlCommand(batches[i], db) { CommandTimeout = 60 };
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    var where = batches.Count == 1 ? name : $"{name} batch {i + 1}/{batches.Count}";
                    throw new InvalidOperationException(
                        $"Schema file {where} failed: {ex.Message}", ex);
                }
            }
            logger?.LogInformation("Applied {File}", name);
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

    // Probe the user DB before the schema files run.  Three cases the
    // schema files can handle, so we return early:
    //   - DB does not exist          -- 002-create-db.sql creates it.
    //   - DB in a non-ONLINE state   -- e.g. RECOVERY_PENDING because a
    //                                   prior migration left FILENAME
    //                                   metadata pointing at files that
    //                                   are not on disk; 001b drops the
    //                                   broken DB and create-db rebuilds.
    //   - DB ONLINE                  -- sanity-check USE (catches the
    //                                   904 window where SQL Server is
    //                                   ONLINE but not yet accepting
    //                                   USE statements).
    // Only RECOVERING (crash recovery on container start) keeps us in
    // the wait loop; everything else falls through immediately.
    private static void WaitForUserDatabaseOnline(string masterConnStr, string dbName, ILogger? logger, int maxSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(maxSeconds);
        Exception? lastError = null;
        var attempt = 0;
        var useSql = $"USE {QuoteIdent(dbName)}; SELECT 1;";
        while (DateTime.UtcNow < deadline)
        {
            attempt++;
            try
            {
                using var db = new SqlConnection(masterConnStr);
                db.Open();
                using var stateCmd = new SqlCommand(
                    "SELECT state_desc FROM sys.databases WHERE name = @n", db);
                stateCmd.Parameters.AddWithValue("@n", dbName);
                var state = stateCmd.ExecuteScalar() as string;
                if (state == null)
                {
                    logger?.LogInformation(
                        "Database {Db} does not exist yet; schema will create it", dbName);
                    return;
                }
                if (state != "ONLINE" && state != "RECOVERING")
                {
                    logger?.LogWarning(
                        "Database {Db} state = {State}; proceeding with schema apply (may recover/recreate)",
                        dbName, state);
                    return;
                }
                if (state == "ONLINE")
                {
                    using var useCmd = new SqlCommand(useSql, db);
                    useCmd.ExecuteScalar();
                    logger?.LogInformation(
                        "Database {Db} accepts USE after {Attempts} attempt(s)", dbName, attempt);
                    return;
                }
                if (attempt == 1 || attempt % 5 == 0)
                {
                    logger?.LogInformation(
                        "Database {Db} state = RECOVERING; waiting (attempt {Attempt})", dbName, attempt);
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt == 1 || attempt % 5 == 0)
                {
                    logger?.LogInformation(
                        "Probe error for database {Db} (attempt {Attempt}): {Message}",
                        dbName, attempt, ex.Message);
                }
            }
            Thread.Sleep(2000);
        }
        throw new InvalidOperationException(
            $"Database {dbName} did not reach a usable state within {maxSeconds}s. " +
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
