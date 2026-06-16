using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Oracle.ManagedDataAccess.Client;

namespace SyncDb;

/// <summary>
/// Core sync engine: throttled, resumable apply of an Oracle change log.
///
/// The source table is a change log: each row carries an operation flag saying
/// whether the target row should be inserted, updated, or deleted. The engine
/// applies those changes to the target table in batches.
///
/// Strategy
/// --------
/// The source is read with <em>keyset pagination</em> on its indexed key column
/// (typically the change log's own sequence):
///
///     SELECT ... WHERE key &gt; :last_key ORDER BY key FETCH FIRST :n ROWS ONLY
///
/// Each query touches only the next slice of the index, so the cost per batch
/// is flat no matter how large the table is — unlike OFFSET pagination, which
/// gets slower the deeper it goes, and unlike one giant open cursor, which
/// risks ORA-01555 (snapshot too old) on long runs.
///
/// Within a batch, <em>every</em> change is applied in the order it arrives —
/// changes are not coalesced, so a key with several changes in one batch has all
/// of them replayed in sequence. To keep that ordering correct, consecutive
/// changes of the same kind are grouped into one array-bound MERGE (inserts and
/// updates) or DELETE, and a new array DML is started whenever the kind switches.
/// Every statement is idempotent, so replaying a batch after a crash is harmless.
///
/// After every batch the engine commits, persists a checkpoint, and sleeps, so
/// the database only ever sees short bursts of light work.
/// </summary>
public sealed partial class TableSync
{
    // How many times to retry a batch after a connection failure before giving up.
    private const int MaxRetries = 4;
    private const int RetryBackoffSeconds = 5;

    private readonly SyncConfig _config;
    private readonly ILogger _log;
    private readonly Dictionary<string, int> _columnIndex;
    private readonly int _keyIndex;
    private readonly int _opIndex;

    private readonly string _selectFirstSql;
    private readonly string _selectNextSql;
    private readonly string _mergeSql;
    private readonly string _deleteSql;

    // Queue mode (processed flag) — null unless PROCESSED_COLUMN is configured.
    private readonly string? _selectPendingSql;
    private readonly string? _markSql;
    private readonly object? _processedPending;
    private readonly object? _processedDone;

    // User-link mode — null/zero unless LINK_TABLE is configured.
    private readonly string? _linkMergeSql;
    private readonly string? _linkDeleteSql;
    private readonly int _linkColumnCount;
    private readonly object? _syncUserId;
    private readonly object? _syncUserSection;

    // Audit/quarantine — null unless HISTORY_TABLE / QUARANTINE_TABLE configured.
    private readonly string? _historyMergeSql;
    private readonly int _historyColumnCount;
    private readonly string? _quarantineMergeSql;
    private readonly string? _quarantineSelectSql;
    private const int HistoryDetailMax = 400;

    // op value -> "upsert" | "delete"
    private readonly Dictionary<string, string> _actions;
    // Business keys whose insert failed: every later change for them is skipped.
    // Loaded from the quarantine table at startup, then kept in sync in memory.
    private readonly HashSet<RowKey> _quarantine = [];

    private OracleConnection? _conn;
    private volatile bool _stopRequested;

    public Checkpoint Checkpoint { get; }

    public TableSync(SyncConfig config, ILoggerFactory loggerFactory)
    {
        _config = config;
        _log = loggerFactory.CreateLogger("syncdb.sync");
        Checkpoint = new Checkpoint(config.CheckpointFile, _log);

        var missingKeys = Transformer.TargetKeyColumns
            .Where(c => !Transformer.TargetColumns.Contains(c))
            .ToArray();
        if (missingKeys.Length > 0)
        {
            throw new InvalidOperationException(
                $"TargetKeyColumns [{string.Join(", ", missingKeys)}] not present in TargetColumns");
        }

        var key = Identifier(config.KeyColumn);
        var source = QualifiedName(config.SourceTable);
        var target = QualifiedName(config.TargetTable);
        var srcCols = string.Join(", ", Transformer.SourceColumns.Select(Identifier));
        var tgtCols = Transformer.TargetColumns.Select(Identifier).ToArray();
        var keyCols = Transformer.TargetKeyColumns.Select(Identifier).ToArray();
        var nonKeyCols = tgtCols.Where(c => !keyCols.Contains(c)).ToArray();

        _selectFirstSql =
            $"SELECT {srcCols} FROM {source} " +
            $"ORDER BY {key} FETCH FIRST :batch_size ROWS ONLY";
        _selectNextSql =
            $"SELECT {srcCols} FROM {source} " +
            $"WHERE {key} > :last_key " +
            $"ORDER BY {key} FETCH FIRST :batch_size ROWS ONLY";

        // Upsert: bind positions follow TargetColumns, i.e. Transform() output.
        var using_ = string.Join(", ", tgtCols.Select((c, i) => $":{i + 1} AS {c}"));
        var on = string.Join(" AND ", keyCols.Select(c => $"t.{c} = s.{c}"));
        var updateSet = string.Join(", ", nonKeyCols.Select(c => $"t.{c} = s.{c}"));
        var insertCols = string.Join(", ", tgtCols);
        var insertVals = string.Join(", ", tgtCols.Select(c => $"s.{c}"));
        _mergeSql =
            $"MERGE INTO {target} t " +
            $"USING (SELECT {using_} FROM dual) s " +
            $"ON ({on}) " +
            $"WHEN MATCHED THEN UPDATE SET {updateSet} " +
            $"WHEN NOT MATCHED THEN INSERT ({insertCols}) VALUES ({insertVals})";

        // Delete: bind positions follow TargetKeyColumns, i.e. TransformKey().
        var where = string.Join(" AND ", keyCols.Select((c, i) => $"{c} = :{i + 1}"));
        _deleteSql = $"DELETE FROM {target} WHERE {where}";

        if (config.ProcessedEnabled)
        {
            // Queue model: read only pending rows, and stamp the rows read this batch
            // (by KEY_COLUMN) as done in the same transaction that applies them.
            //
            // Exception: when quarantine is on, a change row whose business key is
            // quarantined is left PENDING (see ApplyBatch). To stop those pending
            // rows from being re-read forever, the fetch also excludes any key that
            // is in QUARANTINE_TABLE via an anti-join. Net effect: a quarantined key
            // disappears from the queue until you delete it from QUARANTINE_TABLE —
            // then its still-pending rows requeue on their own, no flag reset needed.
            // NOTE: this assumes the change log exposes the target key column(s) under
            // the same name as QUARANTINE_TABLE (true for the shipped ORDER_ID schema).
            var processed = Identifier(config.ProcessedColumn);
            var notQuarantined = config.QuarantineEnabled
                ? $"AND NOT EXISTS (SELECT 1 FROM {QualifiedName(config.QuarantineTable)} q WHERE " +
                  string.Join(" AND ", keyCols.Select(c => $"q.{c} = src.{c}")) + ") "
                : "";
            _selectPendingSql =
                $"SELECT {srcCols} FROM {source} src " +
                $"WHERE {processed} = :pending " +
                notQuarantined +
                $"ORDER BY {key} FETCH FIRST :batch_size ROWS ONLY";
            _markSql = $"UPDATE {source} SET {processed} = :1 WHERE {key} = :2";
            _processedPending = CoerceScalar(config.ProcessedPending);
            _processedDone = CoerceScalar(config.ProcessedDone);
        }

        if (config.LinkEnabled)
        {
            // Each inserted target row also gets a link row carrying its own id, the
            // target business key, the fixed user id, and the fixed user section.
            // MERGE-on-no-match keeps it idempotent on replay. Only the business key,
            // user id, and section are bound (positions :1..:n); the link row's own
            // id is filled by the database as MAX(id)+1.
            //
            // Array binding executes the MERGE once per row in arrival order within
            // the one transaction, so each iteration's MAX(id) sees the rows inserted
            // by earlier iterations — the ids come out sequential and never collide.
            var linkTable = QualifiedName(config.LinkTable);
            var idCol = Identifier(config.LinkIdColumn);
            var userCol = Identifier(config.LinkUserColumn);
            var sectionCol = Identifier(config.LinkSectionColumn);
            var linkBound = keyCols.Append(userCol).Append(sectionCol).ToArray();
            _linkColumnCount = linkBound.Length;
            var linkUsing = string.Join(", ", linkBound.Select((c, i) => $":{i + 1} AS {c}"));
            var linkOn = string.Join(" AND ", linkBound.Select(c => $"t.{c} = s.{c}"));
            var linkInsertCols = string.Join(", ", linkBound.Prepend(idCol));
            var nextId = $"(SELECT NVL(MAX({idCol}), 0) + 1 FROM {linkTable})";
            var linkInsertVals = string.Join(", ", linkBound.Select(c => $"s.{c}").Prepend(nextId));
            _linkMergeSql =
                $"MERGE INTO {linkTable} t " +
                $"USING (SELECT {linkUsing} FROM dual) s " +
                $"ON ({linkOn}) " +
                $"WHEN NOT MATCHED THEN INSERT ({linkInsertCols}) VALUES ({linkInsertVals})";
            // A target delete removes every link row for that business key.
            var linkWhere = string.Join(" AND ", keyCols.Select((c, i) => $"{c} = :{i + 1}"));
            _linkDeleteSql = $"DELETE FROM {linkTable} WHERE {linkWhere}";
            _syncUserId = CoerceScalar(config.SyncUserId);
            _syncUserSection = CoerceScalar(config.SyncUserSection);
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
            // detail, recorded_at. MERGE on the change id keeps it idempotent on
            // replay; the WHEN MATCHED update also refreshes the row when the same
            // change id is *re-processed* — e.g. a previously QUARANTINED change that
            // requeues after its key is cleared, whose outcome is now UPSERTED. Without
            // it the row would keep its stale first outcome.
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

        _actions = new Dictionary<string, string>
        {
            [config.OpInsert] = "upsert",
            [config.OpUpdate] = "upsert",
            [config.OpDelete] = "delete",
        };

        _columnIndex = Transformer.SourceColumns
            .Select((c, i) => (c, i))
            .ToDictionary(t => t.c, t => t.i);
        _keyIndex = Array.IndexOf(Transformer.SourceColumns, config.KeyColumn);
        _opIndex = Array.IndexOf(Transformer.SourceColumns, config.OpColumn);
    }

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
    /// bare (<c>TABLE</c>). Each part is whitelisted like <see cref="Identifier"/>;
    /// these are interpolated into SQL, not bound, so the whitelist guards injection.
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
    /// Classify each change row into a unit of work, preserving order.
    ///
    /// Produces one <see cref="RowEvent"/> per change row read (the full-history
    /// trail) and one <see cref="PlanItem"/> per applicable change. Changes are
    /// <em>not</em> coalesced: several changes to the same target row all survive,
    /// in arrival order, so they are replayed one after another at apply time.
    ///
    /// A row whose insert is rejected by the transformer (and an apply-time insert
    /// failure later) <em>quarantines</em> its business key: that key is added to
    /// <paramref name="newQuarantine"/> for persistence and every later change for
    /// it — in this batch or future ones — is skipped.
    /// </summary>
    private (List<PlanItem> Items, List<RowEvent> Events, List<RowKey> NewQuarantine) PlanBatch(List<object?[]> rows)
    {
        var events = new List<RowEvent>(rows.Count);
        var plan = new List<PlanItem>(rows.Count);
        var newQuarantine = new List<RowKey>();

        foreach (var raw in rows)
        {
            var changeId = raw[_keyIndex];
            var opValue = Convert.ToString(raw[_opIndex], CultureInfo.InvariantCulture) ?? string.Empty;
            var ev = new RowEvent(changeId, opValue);
            events.Add(ev);

            if (!_actions.TryGetValue(opValue, out var action))
            {
                ev.Mark(Outcome.SkippedBadRow, $"unknown {_config.OpColumn} value '{opValue}'");
                _log.LogWarning("Skipping {KeyColumn}={KeyValue}: {Detail}", _config.KeyColumn, changeId, ev.Detail);
                continue;
            }

            var row = new Row(_columnIndex, raw);
            RowKey key;
            try
            {
                key = Transformer.TransformKey(row);
            }
            catch (TransformException exc)
            {
                ev.Mark(Outcome.SkippedBadRow, exc.Message);
                _log.LogWarning("Skipping {KeyColumn}={KeyValue}: {Message}", _config.KeyColumn, changeId, exc.Message);
                continue;
            }
            ev.KeyValues = [.. key.Values];

            if (_quarantine.Contains(key))
            {
                ev.Mark(Outcome.SkippedQuarantined, "business key is quarantined");
                continue;
            }

            var isInsert = opValue == _config.OpInsert;
            object?[] payload;
            try
            {
                payload = action == "upsert" ? Transformer.Transform(row) : [.. key.Values];
            }
            catch (TransformException exc)
            {
                // A rejected insert quarantines the key; a rejected update/delete
                // is just skipped (it never created the row in the first place).
                if (isInsert && _config.QuarantineEnabled)
                {
                    ev.Mark(Outcome.Quarantined, exc.Message);
                    QuarantineKey(key, newQuarantine);
                    _log.LogWarning(
                        "Quarantining {KeyColumn}={KeyValue}: insert rejected: {Message}",
                        _config.KeyColumn, changeId, exc.Message);
                }
                else
                {
                    ev.Mark(Outcome.SkippedBadRow, exc.Message);
                    _log.LogWarning("Skipping {KeyColumn}={KeyValue}: {Message}", _config.KeyColumn, changeId, exc.Message);
                }
                continue;
            }

            plan.Add(new PlanItem(changeId, key, isInsert, action, payload, ev));
        }

        return (plan, events, newQuarantine);
    }

    /// <summary>Add a key to the in-memory quarantine set and the persist list.</summary>
    private void QuarantineKey(RowKey key, List<RowKey> pending)
    {
        if (_quarantine.Add(key))
        {
            pending.Add(key);
        }
    }

    /// <summary>
    /// Whether this change row's business key is quarantined — used to decide it
    /// should stay PENDING in queue mode. False for rows whose key could not be
    /// read (KeyValues empty) and whenever quarantine is disabled.
    /// </summary>
    private bool IsQuarantined(RowEvent ev) =>
        _config.QuarantineEnabled
        && ev.KeyValues.Length == Transformer.TargetKeyColumns.Length
        && _quarantine.Contains(new RowKey(ev.KeyValues));

    /// <summary>
    /// Apply one batch in a single transaction: data changes, user links, the
    /// per-row history, newly quarantined keys, and the processed stamps all
    /// commit together (or not at all).
    ///
    /// When quarantine is enabled a bad row no longer dooms the batch: each data
    /// statement is tried in bulk first, and only if it fails on data is it
    /// retried row by row to isolate the offender (recoverable connection errors
    /// still propagate so the whole batch is retried by <see cref="WithRetries"/>).
    /// </summary>
    private void ApplyBatch(
        List<PlanItem> items, List<RowEvent> events, List<RowKey> newQuarantine)
    {
        using var tx = _conn!.BeginTransaction();
        try
        {
            // Apply every change in arrival order. We cannot group all upserts
            // before all deletes — for a key with delete-then-reinsert that would
            // wrongly leave it deleted — so we only batch a run of consecutive
            // same-kind changes and start a new array DML when the kind switches.
            var run = new List<PlanItem>();
            string? runAction = null;
            foreach (var item in items)
            {
                if (run.Count > 0 && item.Action != runAction)
                {
                    ApplyRun(runAction!, run, tx, newQuarantine);
                    run = [];
                }
                runAction = item.Action;
                run.Add(item);
            }
            if (run.Count > 0)
            {
                ApplyRun(runAction!, run, tx, newQuarantine);
            }

            // Maintain the link table: add a row for each insert that landed and
            // remove rows for each delete that landed, in arrival order.
            if (_config.LinkEnabled)
            {
                ApplyLinks(items, tx);
            }

            if (_config.QuarantineEnabled && newQuarantine.Count > 0)
            {
                var q = newQuarantine.Select(k => k.Values.ToArray()).ToList();
                ExecuteArray(_quarantineMergeSql!, q, Transformer.TargetKeyColumns.Length, tx);
            }

            if (_config.AuditEnabled)
            {
                var history = events.Select(BuildHistoryRow).ToList();
                ExecuteArray(_historyMergeSql!, history, _historyColumnCount, tx);
            }

            // Stamp the consumed change rows last so they are marked done only if
            // their effect (and its history) committed — same transaction. A row
            // whose business key ended up quarantined is left PENDING so that
            // clearing the key from QUARANTINE_TABLE requeues it on its own; every
            // other row (applied, or a keyless/unknown-op bad row) is marked done so
            // it can never stall the queue head. Built here, after apply, so that
            // apply-time quarantines (a FAILED insert) are accounted for too.
            if (_config.ProcessedEnabled)
            {
                var markRows = new List<object?[]>(events.Count);
                foreach (var ev in events)
                {
                    if (!IsQuarantined(ev))
                    {
                        markRows.Add([_processedDone, ev.ChangeId]);
                    }
                }
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
    }

    /// <summary>Apply one run of consecutive same-kind changes via its array DML.</summary>
    private void ApplyRun(string action, List<PlanItem> run, OracleTransaction tx, List<RowKey> newQuarantine)
    {
        if (action == "upsert")
        {
            ApplyOp(_mergeSql, run, Transformer.TargetColumns.Length, Outcome.Upserted, tx, newQuarantine);
        }
        else
        {
            ApplyOp(_deleteSql, run, Transformer.TargetKeyColumns.Length, Outcome.Deleted, tx, newQuarantine);
        }
    }

    /// <summary>
    /// Apply one data statement for a list of planned items, recording the outcome
    /// on each item's event. Without quarantine this is a single array DML whose
    /// failure aborts the batch (the original behavior). With quarantine on, a
    /// data failure rolls back to a savepoint and the rows are retried one at a
    /// time so the bad one is isolated, recorded as FAILED, and (if it was an
    /// insert) its key quarantined — the rest of the batch still applies.
    /// </summary>
    private void ApplyOp(
        string sql, List<PlanItem> items, int columnCount, string success,
        OracleTransaction tx, List<RowKey> newQuarantine)
    {
        if (items.Count == 0)
        {
            return;
        }

        if (!_config.QuarantineEnabled)
        {
            ExecuteArray(sql, items.Select(i => i.Payload).ToList(), columnCount, tx);
            foreach (var item in items)
            {
                item.Event.Mark(success, null);
            }
            return;
        }

        const string savepoint = "syncdb_op";
        tx.Save(savepoint);
        try
        {
            ExecuteArray(sql, items.Select(i => i.Payload).ToList(), columnCount, tx);
            foreach (var item in items)
            {
                item.Event.Mark(success, null);
            }
            return;
        }
        catch (OracleException exc) when (!exc.IsRecoverable)
        {
            _log.LogWarning(
                "Bulk apply of {Count} row(s) failed (ORA-{Code:00000}); isolating bad rows one by one.",
                items.Count, exc.Number);
            // Undo the partially-applied array DML, then redo it row by row.
            tx.Rollback(savepoint);
        }

        foreach (var item in items)
        {
            try
            {
                ExecuteArray(sql, [item.Payload], columnCount, tx);
                item.Event.Mark(success, null);
            }
            catch (OracleException exc) when (!exc.IsRecoverable)
            {
                var detail = $"ORA-{exc.Number:00000}: {FirstLine(exc.Message)}";
                item.Event.Mark(Outcome.Failed, detail);
                if (item.IsInsert)
                {
                    QuarantineKey(item.Key, newQuarantine);
                    _log.LogWarning(
                        "Quarantining {KeyColumn}={KeyValue}: insert failed: {Detail}",
                        _config.KeyColumn, item.ChangeId, detail);
                }
                else
                {
                    _log.LogWarning(
                        "Skipping {KeyColumn}={KeyValue}: apply failed: {Detail}",
                        _config.KeyColumn, item.ChangeId, detail);
                }
            }
        }
    }

    /// <summary>
    /// Maintain the link table for this batch: a row is added for each insert that
    /// landed and removed for each delete that landed. These are applied in arrival
    /// order (grouping consecutive same-kind link ops) so a key's insert/delete
    /// sequence in one batch ends in the right link state.
    /// </summary>
    private void ApplyLinks(List<PlanItem> items, OracleTransaction tx)
    {
        var run = new List<PlanItem>();
        string? runKind = null;
        foreach (var item in items)
        {
            var kind = LinkKind(item);
            if (kind is null)
            {
                continue;
            }
            if (run.Count > 0 && kind != runKind)
            {
                ApplyLinkRun(runKind!, run, tx);
                run = [];
            }
            runKind = kind;
            run.Add(item);
        }
        if (run.Count > 0)
        {
            ApplyLinkRun(runKind!, run, tx);
        }
    }

    /// <summary>"insert" for a landed insert, "delete" for a landed delete, else none.</summary>
    private static string? LinkKind(PlanItem item) =>
        item.IsInsert && item.Event.Outcome == Outcome.Upserted ? "insert"
        : item.Action == "delete" && item.Event.Outcome == Outcome.Deleted ? "delete"
        : null;

    /// <summary>
    /// Apply one run of link inserts or link deletes, isolated like the data apply:
    /// one bulk DML first, and on a data failure a rollback-to-savepoint and a
    /// row-by-row replay. A link op that still fails does <em>not</em> abort the run
    /// or undo its already-applied target change — it is recorded as LINK_FAILED and
    /// skipped. Recoverable errors propagate so the whole batch is retried.
    /// </summary>
    private void ApplyLinkRun(string kind, List<PlanItem> run, OracleTransaction tx)
    {
        var insert = kind == "insert";
        var sql = insert ? _linkMergeSql! : _linkDeleteSql!;
        var columnCount = insert ? _linkColumnCount : Transformer.TargetKeyColumns.Length;

        const string savepoint = "syncdb_link";
        tx.Save(savepoint);
        try
        {
            ExecuteArray(sql, run.Select(i => LinkPayload(i, insert)).ToList(), columnCount, tx);
            return; // all linked/unlinked; rows keep their data outcome
        }
        catch (OracleException exc) when (!exc.IsRecoverable)
        {
            _log.LogWarning(
                "Bulk link {Kind} of {Count} row(s) failed (ORA-{Code:00000}); isolating one by one.",
                kind, run.Count, exc.Number);
            tx.Rollback(savepoint);
        }

        foreach (var item in run)
        {
            try
            {
                ExecuteArray(sql, [LinkPayload(item, insert)], columnCount, tx);
            }
            catch (OracleException exc) when (!exc.IsRecoverable)
            {
                var detail = $"link {kind} failed: ORA-{exc.Number:00000}: {FirstLine(exc.Message)}";
                // The target change is already applied; just flag the bad link op.
                item.Event.Mark(Outcome.LinkFailed, detail);
                _log.LogWarning(
                    "{KeyColumn}={KeyValue}: {Detail} — target change kept, link skipped.",
                    _config.KeyColumn, item.ChangeId, detail);
            }
        }
    }

    private object?[] LinkPayload(PlanItem item, bool insert) =>
        insert ? [.. item.Key.Values, _syncUserId, _syncUserSection] : [.. item.Key.Values];

    /// <summary>Build one SYNC_ROW_HISTORY tuple from a change row's outcome.</summary>
    private static object?[] BuildHistoryRow(RowEvent ev)
    {
        var keyCount = Transformer.TargetKeyColumns.Length;
        var row = new object?[1 + keyCount + 4];
        var i = 0;
        row[i++] = ev.ChangeId;
        for (var k = 0; k < keyCount; k++)
        {
            // KeyValues is empty when the key could not be extracted (bad row).
            row[i++] = ev.KeyValues.Length == keyCount ? ev.KeyValues[k] : null;
        }
        row[i++] = ev.Op;
        row[i++] = ev.Outcome;
        row[i++] = ev.Detail is { Length: > HistoryDetailMax }
            ? ev.Detail[..HistoryDetailMax]
            : ev.Detail;
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
    /// once per row in a single round trip (the equivalent of executemany).
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
    /// Pick an ODP.NET bind type from a column's first non-null value, the way
    /// python-oracledb infers from the data. Defaults to VARCHAR2.
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

                var (items, events, newQuarantine) = PlanBatch(rows);
                WithRetries(() => { ApplyBatch(items, events, newQuarantine); return 0; });
                rowsProcessed += rows.Count;
                lastKey = rows[^1][_keyIndex];
                Checkpoint.Save(lastKey, rowsProcessed);

                var upserted = events.Count(e => e.Outcome == Outcome.Upserted);
                var deleted = events.Count(e => e.Outcome == Outcome.Deleted);
                var failed = events.Count(e => e.Outcome == Outcome.Failed);
                _log.LogInformation(
                    "Batch {Batch}: read {Read} changes -> {Upserts} upserts, {Deletes} deletes, " +
                    "{Failed} failed, {Quarantined} newly quarantined (total {Total}, last {KeyColumn}={KeyValue})",
                    batchNumber, rows.Count, upserted, deleted, failed, newQuarantine.Count,
                    rowsProcessed, _config.KeyColumn, lastKey);

                // A short batch means we are at the tail of the table; loop
                // straight back to confirm completion instead of sleeping.
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
        public const string Upserted = "UPSERTED";
        public const string Deleted = "DELETED";
        public const string SkippedBadRow = "SKIPPED_BADROW";   // unknown op / rejected transform
        public const string SkippedQuarantined = "SKIPPED_QUARANTINED";
        public const string Quarantined = "QUARANTINED";        // this insert failed -> key quarantined
        public const string Failed = "FAILED";                  // DB rejected the apply
        public const string LinkFailed = "LINK_FAILED";         // row upserted, but its user link failed
    }

    /// <summary>
    /// The fate of one change row, accumulated as it is planned and applied and
    /// written out as one history record. <see cref="Outcome"/> starts unset and
    /// every code path stamps it exactly once via <see cref="Mark"/>.
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

    /// <summary>One unit of work plus a link back to its history event.</summary>
    private sealed class PlanItem(
        object? changeId, RowKey key, bool isInsert,
        string action, object?[] payload, RowEvent ev)
    {
        public object? ChangeId { get; } = changeId;
        public RowKey Key { get; } = key;
        public bool IsInsert { get; } = isInsert;            // this change is an insert
        public string Action { get; } = action;              // "upsert" | "delete"
        public object?[] Payload { get; } = payload;
        public RowEvent Event { get; } = ev;
    }
}
