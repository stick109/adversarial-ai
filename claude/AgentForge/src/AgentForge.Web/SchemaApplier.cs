using System.Text;
using Microsoft.Data.SqlClient;

namespace AgentForge.Web;

// Apply db/001_schema.sql at app startup so containerised deployments
// (Railway, fresh docker compose) don't need an external sqlcmd step.
//
// The schema script is idempotent: re-running it is a no-op once the
// DB, tables, columns, and seed rows already exist.  This class:
//   1. Waits for the SQL Server to accept connections (DB container
//      may still be booting when the Web container starts).
//   2. Connects to `master` so the very first batch -- CREATE DATABASE
//      AgentForge -- can run before AgentForge exists.  The script's
//      USE AgentForge; switches the same connection over for the rest
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
