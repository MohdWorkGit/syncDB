using System.Data;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;

namespace SyncDb;

/// <summary>
/// Core sync engine: throttled, resumable apply of an Oracle change log.
///
/// The source table is a change log: each row carries an operation flag saying
/// whether the target row should be inserted, updated, or deleted. The engine
/// reads the source in batches and, for each row, runs the <em>hand-written SQL
/// statement</em> for that operation — one editable <c>.sql</c> file each for
/// insert, update, and delete (see <see cref="SyncConfig"/>). Each <c>:NAME</c>
/// placeholder in those statements is bound from the source row by column name,
/// so the whole transformation lives in SQL you can edit.
///
/// Strategy
/// --------
/// The source is read with <em>keyset pagination</em> on its indexed key column
/// (typically the change log's own sequence):
///
///     SELECT * FROM source WHERE key &gt; :last_key ORDER BY key FETCH FIRST :n ROWS ONLY
///
/// Each query touches only the next slice of the index, so the cost per batch is
/// flat no matter how large the table is. Within a batch every change is applied
/// in arrival order, one row at a time, each on its own savepoint so a single bad
/// row is isolated instead of dooming the batch. Every statement is expected to be
/// idempotent (MERGE / "if exists" deletes), so replaying a batch after a crash is
/// harmless.
///
/// After every batch the engine commits, persists a checkpoint, and sleeps, so
/// the database only ever sees short bursts of light work.
/// </summary>
public sealed partial class TableSync
{
    // How many times to retry a batch after a connection failure before giving up.
    private const int MaxRetries = 4;
    private const int RetryBackoffSeconds = 5;
    private const int HistoryDetailMax = 400;

    private readonly SyncConfig _config;
    private readonly ILogger _log;

    // Hand-written per-operation SQL and the :NAME binds each one references.
    private readonly string _insertSql;
    private readonly string _updateSql;
    private readonly string _deleteSql;
    private readonly string? _linkSql;
    private readonly string[] _insertBinds;
    private readonly string[] _updateBinds;
    private readonly string[] _deleteBinds;
    private readonly string[] _linkBinds = [];

    // SELECT * fetch (keyset + queue variants).
    private readonly string _selectFirstSql;
    private readonly string _selectNextSql;

    // Queue mode (processed flag) — null unless PROCESSED_COLUMN is configured.
    private readonly string? _selectPendingSql;
    private readonly string? _markSql;
    private readonly object? _processedPending;
    private readonly object? _processedDone;

    // Audit/quarantine — null unless HISTORY_TABLE / QUARANTINE_TABLE configured.
    private readonly string? _historyMergeSql;
    private readonly int _historyColumnCount;
    private readonly string? _quarantineMergeSql;
    private readonly string? _quarantineSelectSql;

    // Business keys whose insert failed: every later change for them is skipped.
    // Loaded from the quarantine table at startup, then kept in sync in memory.
    private readonly HashSet<RowKey> _quarantine = [];

    // Column name -> ordinal, resolved from the reader the first time we fetch
    // (SELECT * gives whatever the source table has). Stable across batches.
    private Dictionary<string, int>? _columnOrdinals;
    private int _keyOrdinal = -1;
    private int _opOrdinal = -1;
    private int[] _bkOrdinals = [];

    private OracleConnection? _conn;
    private volatile bool _stopRequested;

    public Checkpoint Checkpoint { get; }

    public TableSync(SyncConfig config, ILoggerFactory loggerFactory)
    {
        _config = config;
        _log = loggerFactory.CreateLogger("syncdb.sync");
        Checkpoint = new Checkpoint(config.CheckpointFile, _log);

        _insertSql = ReadSql(config.InsertSqlFile);
        _updateSql = ReadSql(config.UpdateSqlFile);
        _deleteSql = ReadSql(config.DeleteSqlFile);
        _insertBinds = ScanBinds(_insertSql);
        _updateBinds = ScanBinds(_updateSql);
        _deleteBinds = ScanBinds(_deleteSql);

        if (config.LinkEnabled)
        {
            _linkSql = ReadSql(config.LinkSqlFile);
            _linkBinds = ScanBinds(_linkSql);
            if (!_insertBinds.Contains(config.NewIdBind, StringComparer.OrdinalIgnoreCase))
            {
                throw new ConfigException(
                    $"LINK_SQL_FILE is set, so the insert SQL ({config.InsertSqlFile}) must capture the new " +
                    $"row's id with `RETURNING <id> INTO :{config.NewIdBind}` — no :{config.NewIdBind} bind found.");
            }
            // Every link bind other than the new id must come from an env var.
            foreach (var name in _linkBinds)
            {
                if (string.Equals(name, config.NewIdBind, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (Environment.GetEnvironmentVariable(name) is not { Length: > 0 })
                {
                    throw new ConfigException(
                        $"LINK_SQL_FILE references :{name}, which is resolved from the environment, " +
                        $"but {name} is not set. Add {name}=... to your .env (or environment).");
                }
            }
        }

        var key = Identifier(config.KeyColumn);
        var source = QualifiedName(config.SourceTable);
        var keyCols = config.BusinessKeys.Select(Identifier).ToArray();

        _selectFirstSql =
            $"SELECT * FROM {source} ORDER BY {key} FETCH FIRST :batch_size ROWS ONLY";
        _selectNextSql =
            $"SELECT * FROM {source} WHERE {key} > :last_key " +
            $"ORDER BY {key} FETCH FIRST :batch_size ROWS ONLY";

        if (config.ProcessedEnabled)
        {
            // Queue model: read only pending rows, and stamp the rows read this batch
            // (by KEY_COLUMN) as done in the same transaction that applies them.
            // When quarantine is on, a still-pending row whose business key is
            // quarantined is excluded by an anti-join so it doesn't re-spin the head.
            var processed = Identifier(config.ProcessedColumn);
            var notQuarantined = config.QuarantineEnabled
                ? $"AND NOT EXISTS (SELECT 1 FROM {QualifiedName(config.QuarantineTable)} q WHERE " +
                  string.Join(" AND ", keyCols.Select(c => $"q.{c} = src.{c}")) + ") "
                : "";
            _selectPendingSql =
                $"SELECT * FROM {source} src " +
                $"WHERE {processed} = :pending " +
                notQuarantined +
                $"ORDER BY {key} FETCH FIRST :batch_size ROWS ONLY";
            _markSql = $"UPDATE {source} SET {processed} = :1 WHERE {key} = :2";
            _processedPending = CoerceScalar(config.ProcessedPending);
            _processedDone = CoerceScalar(config.ProcessedDone);
        }

        if (config.QuarantineEnabled)
        {
            // Idempotent: a key already quarantined is left as-is on replay.
            var qTable = QualifiedName(config.QuarantineTable);
            var qOn = string.Join(" AND ", keyCols.Select(c => $"t.{c} = s.{c}"));
            var qUsing = string.Join(", ", keyCols.Select((c, i) => $":{i + 1} AS {c}"));
            var qCols = string.Join(", ", keyCols);
            var qVals = string.Join(", ", keyCols.Select(c => $"s.{c}"));
            _quarantineMergeSql =
                $"MERGE INTO {qTable} t USING (SELECT {qUsing} FROM dual) s " +
                $"ON ({qOn}) WHEN NOT MATCHED THEN INSERT ({qCols}) VALUES ({qVals})";
            _quarantineSelectSql = $"SELECT {qCols} FROM {qTable}";
        }

        if (config.AuditEnabled)
        {
            // One row per change id: CHANGE_ID, business key(s), op, outcome,
            // detail, recorded_at. MERGE on the change id keeps it idempotent and
            // refreshes the row when the same change id is re-processed.
            var hTable = QualifiedName(config.HistoryTable);
            var hNonKey = keyCols
                .Concat(["OPERATION", "OUTCOME", "DETAIL", "RECORDED_AT"])
                .ToArray();
            var hCols = new[] { key }.Concat(hNonKey).ToArray();
            _historyColumnCount = hCols.Length;
            var hUsing = string.Join(", ", hCols.Select((c, i) => $":{i + 1} AS {c}"));
            var hInsertCols = string.Join(", ", hCols);
            var hInsertVals = string.Join(", ", hCols.Select(c => $"s.{c}"));
            var hUpdate = string.Join(", ", hNonKey.Select(c => $"t.{c} = s.{c}"));
            _historyMergeSql =
                $"MERGE INTO {hTable} t USING (SELECT {hUsing} FROM dual) s " +
                $"ON (t.{key} = s.{key}) " +
                $"WHEN MATCHED THEN UPDATE SET {hUpdate} " +
                $"WHEN NOT MATCHED THEN INSERT ({hInsertCols}) VALUES ({hInsertVals})";
        }
    }

    private static string ReadSql(string path)
    {
        var sql = File.ReadAllText(path).Trim();
        // Allow a single trailing semicolon for editor convenience; ODP.NET wants
        // the statement without it.
        if (sql.EndsWith(';'))
        {
            sql = sql[..^1].TrimEnd();
        }
        if (sql.Length == 0)
        {
            throw new ConfigException($"SQL file '{path}' is empty.");
        }
        return sql;
    }

    /// <summary>Distinct <c>:NAME</c> bind placeholders referenced by a statement.</summary>
    private static string[] ScanBinds(string sql) =>
        BindRegex().Matches(sql)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Table/column names come from config, not bind variables — whitelist them.
    /// </summary>
    private static string Identifier(string name)
    {
        if (!IdentifierRegex().IsMatch(name))
        {
            throw new InvalidOperationException($"Invalid Oracle identifier: '{name}'");
        }
        return name;
    }

    /// <summary>
    /// Validate a table name that may be schema-qualified (<c>SCHEMA.TABLE</c>) or
    /// bare (<c>TABLE</c>). Each part is whitelisted like <see cref="Identifier"/>.
    /// </summary>
    private static string QualifiedName(string name)
    {
        if (!QualifiedNameRegex().IsMatch(name))
        {
            throw new InvalidOperationException($"Invalid Oracle table name: '{name}'");
        }
        return name;
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_$#]*$")]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_$#]*(\.[A-Za-z][A-Za-z0-9_$#]*)?$")]
    private static partial Regex QualifiedNameRegex();

    // A bind placeholder :NAME, not preceded by another ':' or word char (so it
    // skips PL/SQL := assignment and :: casts).
    [GeneratedRegex(@"(?<![:\w]):([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex BindRegex();

    /// <summary>
    /// Bind a configured flag/id value as a number when it looks like one, so a
    /// NUMBER processed/user column stays index-friendly; otherwise as text.
    /// </summary>
    private static object CoerceScalar(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : value;

    // -- connection handling -------------------------------------------------

    private void Connect()
    {
        _conn = new OracleConnection(_config.ConnectionString);
        _conn.Open();
        _log.LogInformation("Connected to {Dsn} as {User}", _config.Dsn, _config.User);
    }

    private void Close()
    {
        if (_conn is not null)
        {
            try
            {
                _conn.Close();
                _conn.Dispose();
            }
            catch (OracleException)
            {
                // best effort
            }
            _conn = null;
        }
    }

    private void Reconnect()
    {
        Close();
        Connect();
    }

    // -- batch operations ----------------------------------------------------

    private List<object?[]> FetchBatch(object? lastKey)
    {
        using var cmd = new OracleCommand { Connection = _conn!, BindByName = true };
        if (_config.ProcessedEnabled)
        {
            // Progress is driven by the processed flag, not by the paging key:
            // each batch grabs the next slice of still-pending rows in key order.
            cmd.CommandText = _selectPendingSql!;
            cmd.Parameters.Add(new OracleParameter { ParameterName = "pending", Value = _processedPending });
            cmd.Parameters.Add(new OracleParameter { ParameterName = "batch_size", Value = _config.BatchSize });
        }
        else if (lastKey is null)
        {
            cmd.CommandText = _selectFirstSql;
            cmd.Parameters.Add(new OracleParameter { ParameterName = "batch_size", Value = _config.BatchSize });
        }
        else
        {
            cmd.CommandText = _selectNextSql;
            cmd.Parameters.Add(new OracleParameter { ParameterName = "last_key", Value = lastKey });
            cmd.Parameters.Add(new OracleParameter { ParameterName = "batch_size", Value = _config.BatchSize });
        }

        var rows = new List<object?[]>(_config.BatchSize);
        using var reader = cmd.ExecuteReader();
        // Prefetch the whole batch in one round trip (ODP.NET's arraysize knob).
        reader.FetchSize = cmd.RowSize * _config.BatchSize;
        var fieldCount = reader.FieldCount;
        EnsureColumnMap(reader);
        while (reader.Read())
        {
            var row = new object?[fieldCount];
            for (var i = 0; i < fieldCount; i++)
            {
                var v = reader.GetValue(i);
                row[i] = v is DBNull ? null : v;
            }
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// Resolve column ordinals from the reader the first time we fetch, then
    /// validate that the op/key/business-key columns and every data-SQL bind
    /// actually exist in the source — failing fast on a typo.
    /// </summary>
    private void EnsureColumnMap(OracleDataReader reader)
    {
        if (_columnOrdinals is not null)
        {
            return;
        }

        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            map[reader.GetName(i)] = i;
        }
        _columnOrdinals = map;

        _opOrdinal = RequireColumn(_config.OpColumn, "OP_COLUMN");
        _keyOrdinal = RequireColumn(_config.KeyColumn, "KEY_COLUMN");
        _bkOrdinals = _config.BusinessKeys.Select(c => RequireColumn(c, "BUSINESS_KEY_COLUMNS")).ToArray();

        foreach (var (binds, file) in new[]
        {
            (_insertBinds, _config.InsertSqlFile),
            (_updateBinds, _config.UpdateSqlFile),
            (_deleteBinds, _config.DeleteSqlFile),
        })
        {
            foreach (var name in binds)
            {
                if (string.Equals(name, _config.NewIdBind, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // output bind (RETURNING ... INTO), not a source column
                }
                if (!map.ContainsKey(name))
                {
                    throw new ConfigException(
                        $"{file} binds :{name}, but the source table {_config.SourceTable} has no such column.");
                }
            }
        }
    }

    private int RequireColumn(string name, string label)
    {
        if (_columnOrdinals!.TryGetValue(name, out var i))
        {
            return i;
        }
        throw new ConfigException(
            $"{label}='{name}' is not a column of the source table {_config.SourceTable}.");
    }

    /// <summary>
    /// Apply one batch in a single transaction: each change row in arrival order,
    /// then the per-row history and the processed stamps, all committing together.
    /// Each data row runs on its own savepoint so a non-recoverable failure is
    /// isolated to that row; recoverable (connection) errors propagate so
    /// <see cref="WithRetries"/> retries the whole batch.
    /// </summary>
    private List<RowEvent> ApplyBatch(List<object?[]> rows)
    {
        var events = new List<RowEvent>(rows.Count);
        var newQuarantine = new List<RowKey>();

        using var tx = _conn!.BeginTransaction();
        try
        {
            foreach (var raw in rows)
            {
                events.Add(ProcessRow(raw, tx, newQuarantine));
            }

            if (_config.QuarantineEnabled && newQuarantine.Count > 0)
            {
                var q = newQuarantine.Select(k => k.Values.ToArray()).ToList();
                ExecuteArray(_quarantineMergeSql!, q, _config.BusinessKeys.Length, tx);
            }

            if (_config.AuditEnabled)
            {
                var history = events.Select(BuildHistoryRow).ToList();
                ExecuteArray(_historyMergeSql!, history, _historyColumnCount, tx);
            }

            // Stamp consumed change rows last, so they are marked done only if their
            // effect (and history) committed. A row whose business key ended up
            // quarantined is left PENDING so clearing the key requeues it.
            if (_config.ProcessedEnabled)
            {
                var markRows = events
                    .Where(ev => !IsQuarantined(ev))
                    .Select(ev => new object?[] { _processedDone, ev.ChangeId })
                    .ToList();
                if (markRows.Count > 0)
                {
                    ExecuteArray(_markSql!, markRows, 2, tx);
                }
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
        return events;
    }

    /// <summary>
    /// Apply one change row: pick the SQL for its operation, bind it from the row,
    /// run it on its own savepoint, then (for an inserted row) run the link SQL.
    /// Returns the row's outcome event.
    /// </summary>
    private RowEvent ProcessRow(object?[] raw, OracleTransaction tx, List<RowKey> newQuarantine)
    {
        var changeId = raw[_keyOrdinal];
        var opValue = Convert.ToString(raw[_opOrdinal], CultureInfo.InvariantCulture) ?? string.Empty;
        var ev = new RowEvent(changeId, opValue);

        string sql;
        string[] binds;
        bool isInsert = false, isDelete = false;
        if (opValue == _config.OpInsert) { sql = _insertSql; binds = _insertBinds; isInsert = true; }
        else if (opValue == _config.OpUpdate) { sql = _updateSql; binds = _updateBinds; }
        else if (opValue == _config.OpDelete) { sql = _deleteSql; binds = _deleteBinds; isDelete = true; }
        else
        {
            ev.Mark(Outcome.SkippedBadRow, $"unknown {_config.OpColumn} value '{opValue}'");
            _log.LogWarning("Skipping {KeyColumn}={KeyValue}: {Detail}", _config.KeyColumn, changeId, ev.Detail);
            return ev;
        }

        var key = new RowKey(_bkOrdinals.Select(i => raw[i]).ToArray());
        ev.KeyValues = [.. key.Values];

        if (_quarantine.Contains(key))
        {
            ev.Mark(Outcome.SkippedQuarantined, "business key is quarantined");
            return ev;
        }

        const string savepoint = "syncdb_row";
        tx.Save(savepoint);
        try
        {
            var captureId = isInsert && _config.LinkEnabled;
            var newId = ExecuteData(sql, binds, raw, captureId, tx);
            ev.Mark(isDelete ? Outcome.Deleted : isInsert ? Outcome.Inserted : Outcome.Updated, null);

            if (isInsert && _config.LinkEnabled)
            {
                TryLink(newId, ev, tx);
            }
        }
        catch (OracleException exc) when (!exc.IsRecoverable)
        {
            // Bad data: undo just this row and record it; the rest of the batch
            // still commits. A failed insert quarantines its key when enabled.
            tx.Rollback(savepoint);
            var detail = $"ORA-{exc.Number:00000}: {FirstLine(exc.Message)}";
            ev.Mark(Outcome.Failed, detail);
            if (isInsert && _config.QuarantineEnabled)
            {
                QuarantineKey(key, newQuarantine);
                _log.LogWarning(
                    "Quarantining {KeyColumn}={KeyValue}: insert failed: {Detail}",
                    _config.KeyColumn, changeId, detail);
            }
            else
            {
                _log.LogWarning(
                    "Skipping {KeyColumn}={KeyValue}: apply failed: {Detail}",
                    _config.KeyColumn, changeId, detail);
            }
        }
        // A recoverable OracleException propagates: ApplyBatch rolls back the whole
        // transaction and WithRetries reconnects and retries the batch.
        return ev;
    }

    /// <summary>
    /// Run one data statement for a row, binding each :NAME from the source row by
    /// column name. When <paramref name="captureId"/> is set, the statement's
    /// RETURNING ... INTO :NEW_ID output is captured and returned.
    /// </summary>
    private decimal? ExecuteData(string sql, string[] binds, object?[] raw, bool captureId, OracleTransaction tx)
    {
        using var cmd = new OracleCommand
        {
            Connection = _conn!,
            Transaction = tx,
            CommandText = sql,
            BindByName = true,
        };

        OracleParameter? idParam = null;
        foreach (var name in binds)
        {
            if (string.Equals(name, _config.NewIdBind, StringComparison.OrdinalIgnoreCase))
            {
                idParam = new OracleParameter
                {
                    ParameterName = name,
                    OracleDbType = OracleDbType.Decimal,
                    Direction = ParameterDirection.Output,
                };
                cmd.Parameters.Add(idParam);
            }
            else
            {
                cmd.Parameters.Add(MakeInput(name, raw[_columnOrdinals![name]]));
            }
        }

        cmd.ExecuteNonQuery();
        return captureId ? ToDecimal(idParam?.Value) : null;
    }

    /// <summary>
    /// Run the link statement for a just-inserted row: :NEW_ID is the captured id,
    /// every other bind comes from an env var. Isolated on its own savepoint so a
    /// link failure is recorded as LINK_FAILED and the target insert is kept.
    /// </summary>
    private void TryLink(decimal? newId, RowEvent ev, OracleTransaction tx)
    {
        const string savepoint = "syncdb_link";
        tx.Save(savepoint);
        try
        {
            using var cmd = new OracleCommand
            {
                Connection = _conn!,
                Transaction = tx,
                CommandText = _linkSql!,
                BindByName = true,
            };
            foreach (var name in _linkBinds)
            {
                var value = string.Equals(name, _config.NewIdBind, StringComparison.OrdinalIgnoreCase)
                    ? (object?)newId
                    : CoerceScalar(Environment.GetEnvironmentVariable(name) ?? string.Empty);
                cmd.Parameters.Add(MakeInput(name, value));
            }
            cmd.ExecuteNonQuery();
        }
        catch (OracleException exc) when (!exc.IsRecoverable)
        {
            tx.Rollback(savepoint);
            var detail = $"link failed: ORA-{exc.Number:00000}: {FirstLine(exc.Message)}";
            ev.Mark(Outcome.LinkFailed, detail);
            _log.LogWarning(
                "{KeyColumn}={KeyValue}: {Detail} — target insert kept, link skipped.",
                _config.KeyColumn, ev.ChangeId, detail);
        }
    }

    private static OracleParameter MakeInput(string name, object? value) => new()
    {
        ParameterName = name,
        Direction = ParameterDirection.Input,
        Value = value ?? DBNull.Value,
    };

    private static decimal? ToDecimal(object? value) => value switch
    {
        null or DBNull => null,
        OracleDecimal { IsNull: true } => null,
        OracleDecimal d => d.Value,
        decimal d => d,
        _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
    };

    /// <summary>Add a key to the in-memory quarantine set and the persist list.</summary>
    private void QuarantineKey(RowKey key, List<RowKey> pending)
    {
        if (_quarantine.Add(key))
        {
            pending.Add(key);
        }
    }

    /// <summary>
    /// Whether this change row's business key is quarantined — used to keep it
    /// PENDING in queue mode. False when its key could not be read or quarantine
    /// is disabled.
    /// </summary>
    private bool IsQuarantined(RowEvent ev) =>
        _config.QuarantineEnabled
        && ev.KeyValues.Length == _config.BusinessKeys.Length
        && _quarantine.Contains(new RowKey(ev.KeyValues));

    /// <summary>Build one history tuple (change id, business key(s), op, outcome, detail, time).</summary>
    private object?[] BuildHistoryRow(RowEvent ev)
    {
        var keyCount = _config.BusinessKeys.Length;
        var row = new object?[1 + keyCount + 4];
        var i = 0;
        row[i++] = ev.ChangeId;
        for (var k = 0; k < keyCount; k++)
        {
            row[i++] = ev.KeyValues.Length == keyCount ? ev.KeyValues[k] : null;
        }
        row[i++] = ev.Op;
        row[i++] = ev.Outcome;
        row[i++] = ev.Detail is { Length: > HistoryDetailMax } ? ev.Detail[..HistoryDetailMax] : ev.Detail;
        row[i] = DateTime.Now;
        return row;
    }

    private static string FirstLine(string message)
    {
        var nl = message.IndexOf('\n');
        return (nl < 0 ? message : message[..nl]).Trim();
    }

    /// <summary>Load already-quarantined keys so their changes are skipped on sight.</summary>
    private void LoadQuarantine()
    {
        if (!_config.QuarantineEnabled)
        {
            return;
        }

        using var cmd = new OracleCommand { Connection = _conn!, CommandText = _quarantineSelectSql! };
        using var reader = cmd.ExecuteReader();
        var fieldCount = reader.FieldCount;
        var loaded = 0;
        while (reader.Read())
        {
            var values = new object?[fieldCount];
            for (var i = 0; i < fieldCount; i++)
            {
                var v = reader.GetValue(i);
                values[i] = v is DBNull ? null : v;
            }
            _quarantine.Add(new RowKey(values));
            loaded++;
        }
        if (loaded > 0)
        {
            _log.LogInformation("Loaded {Count} quarantined key(s) from {Table}.", loaded, _config.QuarantineTable);
        }
    }

    /// <summary>
    /// Run one array-bound DML statement: array binding executes <paramref name="sql"/>
    /// once per row in a single round trip. Used for the bookkeeping writes
    /// (history, quarantine, processed marks).
    /// </summary>
    private void ExecuteArray(string sql, List<object?[]> rows, int columnCount, OracleTransaction tx)
    {
        using var cmd = new OracleCommand
        {
            Connection = _conn!,
            Transaction = tx,
            CommandText = sql,
            BindByName = false, // positional binds (:1, :2, ...)
            ArrayBindCount = rows.Count,
        };

        for (var col = 0; col < columnCount; col++)
        {
            var column = new object[rows.Count];
            for (var i = 0; i < rows.Count; i++)
            {
                column[i] = rows[i][col] ?? DBNull.Value;
            }
            cmd.Parameters.Add(new OracleParameter
            {
                OracleDbType = InferType(column),
                Value = column,
            });
        }

        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Pick an ODP.NET bind type from a column's first non-null value. Defaults to
    /// VARCHAR2.
    /// </summary>
    private static OracleDbType InferType(object[] column)
    {
        foreach (var v in column)
        {
            if (v is DBNull)
            {
                continue;
            }
            return v switch
            {
                string => OracleDbType.Varchar2,
                DateTime => OracleDbType.TimeStamp,
                decimal => OracleDbType.Decimal,
                long => OracleDbType.Int64,
                int => OracleDbType.Int32,
                short or byte => OracleDbType.Int16,
                double or float => OracleDbType.BinaryDouble,
                bool => OracleDbType.Int32,
                _ => OracleDbType.Varchar2,
            };
        }
        return OracleDbType.Varchar2;
    }

    // -- main loop -----------------------------------------------------------

    public void Run()
    {
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);

        // In queue mode the processed flag drives resume, so the checkpoint key is
        // not used for paging; load it only in keyset mode.
        var lastKey = _config.ProcessedEnabled ? null : Checkpoint.Load();
        long rowsProcessed = 0;
        var batchNumber = 0;
        var started = System.Diagnostics.Stopwatch.StartNew();

        if (_config.ProcessedEnabled)
        {
            _log.LogInformation(
                "Consuming {Source} as a queue: reading {Column}={Pending}, stamping {Done} when done.",
                _config.SourceTable, _config.ProcessedColumn, _config.ProcessedPending, _config.ProcessedDone);
        }

        Connect();
        LoadQuarantine();
        try
        {
            while (!_stopRequested)
            {
                batchNumber++;
                var capturedKey = lastKey;
                var rows = WithRetries(() => FetchBatch(capturedKey));
                if (rows.Count == 0)
                {
                    _log.LogInformation(
                        "Done. {Rows} change rows applied in {Batches} batches ({Elapsed:F1}s).",
                        rowsProcessed, batchNumber - 1, started.Elapsed.TotalSeconds);
                    Checkpoint.Clear();
                    return;
                }

                var events = WithRetries(() => ApplyBatch(rows));
                rowsProcessed += rows.Count;
                lastKey = rows[^1][_keyOrdinal];
                Checkpoint.Save(lastKey, rowsProcessed);

                var inserted = events.Count(e => e.Outcome == Outcome.Inserted);
                var updated = events.Count(e => e.Outcome == Outcome.Updated);
                var deleted = events.Count(e => e.Outcome == Outcome.Deleted);
                var failed = events.Count(e => e.Outcome == Outcome.Failed);
                _log.LogInformation(
                    "Batch {Batch}: read {Read} changes -> {Inserts} inserts, {Updates} updates, " +
                    "{Deletes} deletes, {Failed} failed (total {Total}, last {KeyColumn}={KeyValue})",
                    batchNumber, rows.Count, inserted, updated, deleted, failed,
                    rowsProcessed, _config.KeyColumn, lastKey);

                // A short batch means we are at the tail of the table; loop straight
                // back to confirm completion instead of sleeping.
                if (rows.Count == _config.BatchSize)
                {
                    _log.LogDebug("Sleeping {Seconds:F1}s", _config.SleepSeconds);
                    Sleep(_config.SleepSeconds);
                }
            }

            _log.LogInformation(
                "Stopped on request after {Rows} rows. Checkpoint saved at {KeyColumn}={KeyValue}; " +
                "rerun to resume.", rowsProcessed, _config.KeyColumn, lastKey);
        }
        finally
        {
            Close();
        }
    }

    private void OnSignal(PosixSignalContext context)
    {
        // Finish the current batch instead of letting the runtime terminate us.
        context.Cancel = true;
        _log.LogInformation("Received signal {Signal} — will stop after the current batch.", context.Signal);
        _stopRequested = true;
    }

    /// <summary>Sleep in small slices so a stop signal is honored promptly.</summary>
    private void Sleep(double seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!_stopRequested && DateTime.UtcNow < deadline)
        {
            var remaining = (deadline - DateTime.UtcNow).TotalSeconds;
            Thread.Sleep(TimeSpan.FromSeconds(Math.Min(0.5, Math.Max(0.0, remaining))));
        }
    }

    private T WithRetries<T>(Func<T> fn)
    {
        var delay = RetryBackoffSeconds;
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                return fn();
            }
            catch (OracleException exc)
            {
                if (!exc.IsRecoverable || attempt == MaxRetries)
                {
                    throw;
                }
                _log.LogWarning(
                    "Recoverable DB error (ORA-{Code:00000}) on attempt {Attempt}/{Max}; reconnecting in {Delay}s",
                    exc.Number, attempt, MaxRetries, delay);
                Thread.Sleep(TimeSpan.FromSeconds(delay));
                delay *= 2;
                Reconnect();
            }
        }
        throw new InvalidOperationException("unreachable"); // loop either returns or throws
    }

    // -- per-row classification ----------------------------------------------

    /// <summary>Outcome recorded in the history table for one change row.</summary>
    private static class Outcome
    {
        public const string Inserted = "INSERTED";
        public const string Updated = "UPDATED";
        public const string Deleted = "DELETED";
        public const string SkippedBadRow = "SKIPPED_BADROW";   // unknown op
        public const string SkippedQuarantined = "SKIPPED_QUARANTINED";
        public const string Failed = "FAILED";                  // DB rejected the apply
        public const string LinkFailed = "LINK_FAILED";         // row inserted, but its link failed
    }

    /// <summary>
    /// The fate of one change row, written out as one history record.
    /// <see cref="Outcome"/> starts unset and every path stamps it via <see cref="Mark"/>.
    /// </summary>
    private sealed class RowEvent(object? changeId, string op)
    {
        public object? ChangeId { get; } = changeId;
        public string Op { get; } = op;
        public object?[] KeyValues { get; set; } = [];
        public string Outcome { get; private set; } = "";
        public string? Detail { get; private set; }

        public void Mark(string outcome, string? detail)
        {
            Outcome = outcome;
            Detail = detail;
        }
    }
}
