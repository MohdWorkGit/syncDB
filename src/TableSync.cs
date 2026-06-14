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
/// Within a batch, changes are deduplicated per target business key keeping the
/// <em>latest</em> change (rows arrive in key order, so later wins): an insert
/// followed by a delete nets out to a delete, a delete followed by a re-insert
/// nets out to an upsert. Inserts and updates are then applied with one
/// array-bound MERGE and deletes with one array-bound DELETE — both idempotent,
/// so replaying a batch after a crash is harmless.
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
        var source = Identifier(config.SourceTable);
        var target = Identifier(config.TargetTable);
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

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_$#]*$")]
    private static partial Regex IdentifierRegex();

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
        if (lastKey is null)
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
    /// Turn raw change-log rows into deduplicated upsert/delete work lists.
    ///
    /// Rows arrive ordered by the change key, so for several changes to the
    /// same target row only the last one matters: overwriting the dictionary
    /// entry on repeat gives last-wins semantics.
    /// </summary>
    private (List<object?[]> Upserts, List<object?[]> Deletes) PlanBatch(List<object?[]> rows)
    {
        var ops = new Dictionary<string, string>
        {
            [_config.OpInsert] = "upsert",
            [_config.OpUpdate] = "upsert",
            [_config.OpDelete] = "delete",
        };

        // business key -> ("upsert", target tuple) | ("delete", key tuple)
        var plan = new Dictionary<RowKey, (string Action, object?[] Payload)>();
        foreach (var raw in rows)
        {
            var opValue = Convert.ToString(raw[_opIndex], CultureInfo.InvariantCulture) ?? string.Empty;
            if (!ops.TryGetValue(opValue, out var action))
            {
                _log.LogWarning(
                    "Skipping {KeyColumn}={KeyValue}: unknown {OpColumn} value '{OpValue}'",
                    _config.KeyColumn, raw[_keyIndex], _config.OpColumn, raw[_opIndex]);
                continue;
            }

            var row = new Row(_columnIndex, raw);
            RowKey key;
            object?[] payload;
            try
            {
                key = Transformer.TransformKey(row);
                payload = action == "upsert"
                    ? Transformer.Transform(row)
                    : key.Values.ToArray();
            }
            catch (TransformException exc)
            {
                _log.LogWarning("Skipping row: {Message}", exc.Message);
                continue;
            }

            plan[key] = (action, payload);
        }

        var upserts = new List<object?[]>();
        var deletes = new List<object?[]>();
        foreach (var (action, payload) in plan.Values)
        {
            if (action == "upsert")
            {
                upserts.Add(payload);
            }
            else
            {
                deletes.Add(payload);
            }
        }
        return (upserts, deletes);
    }

    /// <summary>
    /// Apply one batch in a single transaction.
    ///
    /// After dedup each business key appears in exactly one list, so the order
    /// between the MERGE pass and the DELETE pass does not matter. Both
    /// statements are idempotent, making crash-replays harmless.
    /// </summary>
    private void ApplyBatch(List<object?[]> upserts, List<object?[]> deletes)
    {
        using var tx = _conn!.BeginTransaction();
        try
        {
            if (upserts.Count > 0)
            {
                ExecuteArray(_mergeSql, upserts, Transformer.TargetColumns.Length, tx);
            }
            if (deletes.Count > 0)
            {
                ExecuteArray(_deleteSql, deletes, Transformer.TargetKeyColumns.Length, tx);
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
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

        var lastKey = Checkpoint.Load();
        long rowsProcessed = 0;
        var batchNumber = 0;
        var started = System.Diagnostics.Stopwatch.StartNew();

        Connect();
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

                var (upserts, deletes) = PlanBatch(rows);
                WithRetries(() => { ApplyBatch(upserts, deletes); return 0; });
                rowsProcessed += rows.Count;
                lastKey = rows[^1][_keyIndex];
                Checkpoint.Save(lastKey, rowsProcessed);

                _log.LogInformation(
                    "Batch {Batch}: read {Read} changes -> {Upserts} upserts, {Deletes} deletes " +
                    "(total {Total}, last {KeyColumn}={KeyValue})",
                    batchNumber, rows.Count, upserts.Count, deletes.Count,
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
}
