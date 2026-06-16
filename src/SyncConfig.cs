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
    string LogLevel,
    string LogFile,
    string LinkTable,
    string LinkIdColumn,
    string LinkUserColumn,
    string LinkSectionColumn,
    string SyncUserId,
    string SyncUserSection,
    string ProcessedColumn,
    string ProcessedPending,
    string ProcessedDone,
    string HistoryTable,
    string QuarantineTable)
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

        return Validate(new SyncConfig(
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
            LogLevel: Get("LOG_LEVEL", "INFO"),
            LogFile: Get("LOG_FILE", ""),
            // Link table: when set, every change row with OP_INSERT also writes a
            // row connecting the new target row to a user. The link row carries its
            // own id (set to MAX(id)+1), the target business key, the fixed user id,
            // and the fixed user section.
            LinkTable: Get("LINK_TABLE", ""),
            LinkIdColumn: Get("LINK_ID_COLUMN", "LINK_ID"),
            LinkUserColumn: Get("LINK_USER_COLUMN", "USER_ID"),
            LinkSectionColumn: Get("LINK_SECTION_COLUMN", "USER_SECTION"),
            SyncUserId: Get("SYNC_USER_ID", ""),
            SyncUserSection: Get("SYNC_USER_SECTION", ""),
            // Processed column: when set, the source acts as a work queue — only
            // rows whose flag equals PROCESSED_PENDING are read, and they are
            // stamped PROCESSED_DONE in the same transaction that applies them.
            ProcessedColumn: Get("PROCESSED_COLUMN", ""),
            ProcessedPending: Get("PROCESSED_PENDING", "0"),
            ProcessedDone: Get("PROCESSED_DONE", "1"),
            // Audit/quarantine: when HISTORY_TABLE is set, every change row's
            // outcome is recorded; when QUARANTINE_TABLE is set, a row whose
            // insert fails is quarantined so all its later changes are skipped.
            HistoryTable: Get("HISTORY_TABLE", ""),
            QuarantineTable: Get("QUARANTINE_TABLE", "")));

        static SyncConfig Validate(SyncConfig cfg)
        {
            if (cfg.LinkEnabled && cfg.SyncUserId.Length == 0)
            {
                throw new ConfigException(
                    "LINK_TABLE is set but SYNC_USER_ID is empty. Set SYNC_USER_ID to the " +
                    "user id to record for each inserted row, or clear LINK_TABLE to disable linking.");
            }
            if (cfg.LinkEnabled && cfg.SyncUserSection.Length == 0)
            {
                throw new ConfigException(
                    "LINK_TABLE is set but SYNC_USER_SECTION is empty. Set SYNC_USER_SECTION to the " +
                    "section to record for each inserted row, or clear LINK_TABLE to disable linking.");
            }
            return cfg;
        }
    }

    /// <summary>Whether to write a user-link row for each inserted target row.</summary>
    public bool LinkEnabled => LinkTable.Length > 0;

    /// <summary>Whether the source is consumed as a queue via a processed flag.</summary>
    public bool ProcessedEnabled => ProcessedColumn.Length > 0;

    /// <summary>Whether each change row's outcome is recorded to a history table.</summary>
    public bool AuditEnabled => HistoryTable.Length > 0;

    /// <summary>
    /// Whether a row whose insert fails is quarantined. When on, a failed insert
    /// no longer aborts the batch — it is isolated, recorded, and all later
    /// changes for that business key are skipped.
    /// </summary>
    public bool QuarantineEnabled => QuarantineTable.Length > 0;

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
