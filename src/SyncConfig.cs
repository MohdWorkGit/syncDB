using System.Globalization;

namespace SyncDb;

/// <summary>
/// Configuration loaded from environment variables (optionally via a .env file).
/// Immutable; use a <c>with</c> expression to produce a copy with selected
/// overrides, the equivalent of Python's <c>dataclasses.replace</c>.
/// </summary>
public sealed record SyncConfig(
    string User,
    string Password,
    string Dsn,
    int BatchSize,
    double SleepSeconds,
    string SourceTable,
    string TargetTable,
    string KeyColumn,
    string OpColumn,
    string OpInsert,
    string OpUpdate,
    string OpDelete,
    string CheckpointFile,
    string LogLevel)
{
    public static SyncConfig FromEnvironment()
    {
        var missing = new[] { "ORACLE_USER", "ORACLE_PASSWORD", "ORACLE_DSN" }
            .Where(v => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v)))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new ConfigException(
                $"Missing required environment variables: {string.Join(", ", missing)}. " +
                "Copy .env.example to .env and fill it in.");
        }

        return new SyncConfig(
            User: Required("ORACLE_USER"),
            Password: Required("ORACLE_PASSWORD"),
            Dsn: Required("ORACLE_DSN"),
            BatchSize: GetInt("BATCH_SIZE", 1000),
            SleepSeconds: GetDouble("SLEEP_SECONDS", 5),
            SourceTable: Get("SOURCE_TABLE", "ORDER_CHANGES"),
            TargetTable: Get("TARGET_TABLE", "ORDER_SUMMARY"),
            KeyColumn: Get("KEY_COLUMN", "CHANGE_ID"),
            OpColumn: Get("OP_COLUMN", "OPERATION"),
            OpInsert: Get("OP_INSERT", "I"),
            OpUpdate: Get("OP_UPDATE", "U"),
            OpDelete: Get("OP_DELETE", "D"),
            CheckpointFile: Get("CHECKPOINT_FILE", ".sync_checkpoint.json"),
            LogLevel: Get("LOG_LEVEL", "INFO"));
    }

    /// <summary>Build the ODP.NET connection string from the user/password/DSN.</summary>
    public string ConnectionString =>
        $"User Id={User};Password={Password};Data Source={Dsn}";

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name)!;

    private static string Get(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

    private static int GetInt(string name, int fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v
            ? int.Parse(v, CultureInfo.InvariantCulture)
            : fallback;

    private static double GetDouble(string name, double fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v
            ? double.Parse(v, CultureInfo.InvariantCulture)
            : fallback;
}

/// <summary>Thrown when required configuration is missing or invalid.</summary>
public sealed class ConfigException(string message) : Exception(message);
