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
    string KeyColumn,
    string OpColumn,
    string OpInsert,
    string OpUpdate,
    string OpDelete,
    string BusinessKeyColumns,
    string InsertSqlFile,
    string UpdateSqlFile,
    string DeleteSqlFile,
    string LinkSqlFile,
    string NewIdBind,
    string CheckpointFile,
    string LogLevel,
    string LogFile,
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
            KeyColumn: Get("KEY_COLUMN", "CHANGE_ID"),
            OpColumn: Get("OP_COLUMN", "OPERATION"),
            OpInsert: Get("OP_INSERT", "I"),
            OpUpdate: Get("OP_UPDATE", "U"),
            OpDelete: Get("OP_DELETE", "D"),
            // Business-key column(s) on the SOURCE (comma-separated), used only to
            // track quarantine and history — not to build any SQL. The apply SQL
            // is hand-written (see below), so the key is no longer derived from it.
            BusinessKeyColumns: Get("BUSINESS_KEY_COLUMNS", "ORDER_ID"),
            // Hand-written SQL, one statement per file, picked by the source row's
            // operation. Edit these to adapt the sync to your tables; the engine
            // binds each :NAME placeholder from the source row by column name.
            InsertSqlFile: Get("INSERT_SQL_FILE", "sql/insert.sql"),
            UpdateSqlFile: Get("UPDATE_SQL_FILE", "sql/update.sql"),
            DeleteSqlFile: Get("DELETE_SQL_FILE", "sql/delete.sql"),
            // Link SQL (optional): when set, it runs after each successful insert
            // with the new target row's id (bound as :NEW_ID) plus any env-var
            // binds it references (e.g. :SYNC_USER_ID). Nothing from the source
            // row. The insert SQL must capture the new id with RETURNING ... INTO
            // :NEW_ID. Leave blank to disable linking.
            LinkSqlFile: Get("LINK_SQL_FILE", ""),
            NewIdBind: Get("NEW_ID_BIND", "NEW_ID"),
            CheckpointFile: Get("CHECKPOINT_FILE", ".sync_checkpoint.json"),
            LogLevel: Get("LOG_LEVEL", "INFO"),
            LogFile: Get("LOG_FILE", ""),
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
            foreach (var (label, path) in new[]
            {
                ("INSERT_SQL_FILE", cfg.InsertSqlFile),
                ("UPDATE_SQL_FILE", cfg.UpdateSqlFile),
                ("DELETE_SQL_FILE", cfg.DeleteSqlFile),
            })
            {
                if (path.Length == 0)
                {
                    throw new ConfigException($"{label} is required (path to the .sql statement for that operation).");
                }
                if (!File.Exists(path))
                {
                    throw new ConfigException($"{label} points to '{path}', which does not exist.");
                }
            }
            if (cfg.LinkEnabled && !File.Exists(cfg.LinkSqlFile))
            {
                throw new ConfigException($"LINK_SQL_FILE points to '{cfg.LinkSqlFile}', which does not exist.");
            }
            if (cfg.BusinessKeyColumns.Trim().Length == 0)
            {
                throw new ConfigException("BUSINESS_KEY_COLUMNS must name at least one source column.");
            }
            return cfg;
        }
    }

    /// <summary>The business-key column names, split from BUSINESS_KEY_COLUMNS.</summary>
    public string[] BusinessKeys => BusinessKeyColumns
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Whether a link row is written after each inserted target row.</summary>
    public bool LinkEnabled => LinkSqlFile.Length > 0;

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
