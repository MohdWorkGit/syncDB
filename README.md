# syncDB

Throttled, resumable apply of a **very large Oracle change-log table** onto a
target table with a **different structure**, without hammering the database.

Each source row carries an **operation column** — insert (`I`), update (`U`),
or delete (`D`) — that says what to do to the target. The engine is a thin
driver: for each row it picks the **hand-written SQL statement for that
operation** (one editable `.sql` file each) and runs it, binding `:NAME`
placeholders from the source row by column name. The whole source→target
transformation lives in those SQL files, not in C#.

The program works in cycles: read a batch of change rows → for each row run its
op's SQL (one row at a time) → commit → **sleep** → repeat until the source is
exhausted. Between cycles the database is completely idle, so a multi-hour sync
only ever produces short, light bursts of load.

```
┌─────────────────┐  batch   ┌──────────────┐  insert.sql / update.sql  ┌──────────────┐
│ CHANGE LOG table │ ───────▶ │ pick SQL by  │ ────────────────────────▶ │ TARGET table │
│ (huge: I/U/D     │  keyset  │  OPERATION,  │  delete.sql               │ (new shape)  │
│  per row)        │  paging  │ bind by name │  + commit                 │              │
└─────────────────┘          └──────────────┘                           └──────────────┘
        ▲                                                                      │
        └──────────────────── sleep N seconds, repeat ◀────────────────────────┘
```

Built on **.NET 10** (C# 14) and [ODP.NET Core](https://www.nuget.org/packages/Oracle.ManagedDataAccess.Core)
in fully managed mode — no Oracle Instant Client installation needed.

See [docs/workflow.md](docs/workflow.md) for the full per-row workflow and
failure-handling diagrams.

## How operations are applied

| `OPERATION` value | SQL file run        | Binds                          |
|-------------------|---------------------|--------------------------------|
| `I` (insert)      | `INSERT_SQL_FILE`   | source columns (+ `:NEW_ID` out)|
| `U` (update)      | `UPDATE_SQL_FILE`   | source columns                 |
| `D` (delete)      | `DELETE_SQL_FILE`   | source columns (usually the key)|

For each source row the engine reads `OP_COLUMN`, picks the matching SQL file,
and runs it once, binding every `:NAME` placeholder from that row's columns by
name (`SELECT *` from the source makes all columns available). The statements
are yours to edit — put the field merges, date building, code maps, and computed
totals directly in the SQL. Rows are applied **one at a time, in arrival order**,
each on its own savepoint, so one bad row is isolated instead of dooming the
batch. Make each statement idempotent (the shipped delete/update are no-ops when
the row is absent) so a crash-replayed batch is harmless.

The operation column name and its three values are configurable
(`OP_COLUMN`, `OP_INSERT`, `OP_UPDATE`, `OP_DELETE`); rows with any other
value are logged and skipped (`SKIPPED_BADROW`).

## Linking inserted rows to a user

Set `LINK_SQL_FILE` and, after **each successful insert**, the engine runs that
statement once to connect the new target row to a user — in the **same
transaction** as the insert. Nothing in the link row comes from the source: it is
built from

- `:NEW_ID` — the id of the row the insert just created, captured from the
  insert SQL's `RETURNING <id> INTO :NEW_ID` clause (bind name set by
  `NEW_ID_BIND`, default `NEW_ID`); and
- any other `:NAME` it references, resolved from an **environment variable** of
  the same name (e.g. `:SYNC_USER_ID` → `SYNC_USER_ID`).

So the insert SQL must end with `RETURNING <id_col> INTO :NEW_ID`, and the target
table must have an id column to return (the example gives `ORDER_SUMMARY` an
identity `SUMMARY_ID`). Only inserts are linked. A target **delete** does not
unlink anything in the engine — put that in your `delete.sql` / schema instead
(the example link table uses an `ON DELETE CASCADE` foreign key on `SUMMARY_ID`).

A link statement that fails on data (bad value, constraint, missing table) never
aborts the run: it is rolled back to its own savepoint, recorded as `LINK_FAILED`,
and skipped — the target row stays inserted. Only recoverable connection errors
retry the whole batch. Leave `LINK_SQL_FILE` blank to disable linking.

## Consuming the source as a queue (processed flag)

Set `PROCESSED_COLUMN` and the source behaves like a work queue instead of using
keyset paging:

- each batch reads only rows whose flag equals `PROCESSED_PENDING`
  (`WHERE processed = :pending ORDER BY key FETCH FIRST :n`);
- after applying, **every** row read this batch is stamped `PROCESSED_DONE` in
  the *same transaction* — so a row is marked done only if its effect committed,
  and replay is safe.

Rows that are skipped for a non-recoverable reason (unknown operation) are
stamped done too, so a poison row at the head of
the queue can never stall progress. The **one exception** is a row whose business
key is quarantined: it is left pending on purpose so it can be requeued just by
clearing the key (see below). Those pending rows are excluded from the fetch by an
anti-join on `QUARANTINE_TABLE`, so they never re-spin the queue head. Because the
flag itself records progress, resume is automatic and the checkpoint file is no
longer needed for correctness. Index the source on `(processed, key)` so each
batch stays a cheap range scan. Leave `PROCESSED_COLUMN` blank to keep the
keyset + checkpoint behavior.

## Failed inserts: quarantine + per-row history

A change row's insert fails when Oracle rejects the DML (constraint, datatype, a
bad date in `TO_DATE`, …). Because rows are applied one at a time, a failure is
already **isolated**: it is rolled back to that row's savepoint and recorded
`FAILED`, and the rest of the batch still commits (recoverable *connection* errors
still bubble up and retry the whole batch). With `QUARANTINE_TABLE` set, a failed
insert additionally:

- **quarantines** the offending business key — written to `QUARANTINE_TABLE`
  and, from then on, **every later change for that key (update or delete) is
  skipped**. The quarantine set is loaded into memory at startup, so it survives
  restarts. Without it, the `U` that follows a failed `I` would simply run and
  update zero rows.

To un-quarantine a row once the data is fixed, **delete its key from
`QUARANTINE_TABLE`** and restart the sync. In queue mode that is all you need: the
key's change rows were kept pending, so once the quarantine row is gone the fetch
stops excluding them and they requeue on their own. (In keyset mode there is no
pending flag, so instead insert a fresh corrected change row or rerun with
`--restart`.) The quarantine set is loaded at startup, so the delete only takes
effect on the next run.

With `HISTORY_TABLE` set, **every change row** gets one audit record — its
`CHANGE_ID`, business key, operation, and an `OUTCOME`:

| Outcome | Meaning |
|---------|---------|
| `INSERTED` / `UPDATED` / `DELETED` | applied to the target |
| `SKIPPED_BADROW`         | unknown operation value |
| `SKIPPED_QUARANTINED`    | the key was already quarantined |
| `FAILED`                 | Oracle rejected the apply (a failed `I` also quarantines the key) |
| `LINK_FAILED`            | row inserted, but its user-link insert failed (link skipped) |

The history write is part of the same transaction as the apply and is keyed on
`CHANGE_ID`, so it commits atomically with the change and is idempotent on replay.
If the **same** change id is later *re-processed* — e.g. a change for a key that
requeues after the key is cleared from `QUARANTINE_TABLE` — the history row is
**updated** in place to the new outcome and timestamp, so it always reflects the
latest run rather than the stale first attempt. Trace one row's life with
`SELECT * FROM SYNC_ROW_HISTORY WHERE order_id = :id ORDER BY change_id`.

Set `LOG_FILE` to also append the program log to a file alongside the console.

## Why it stays light on the DB

- **Keyset pagination** (`WHERE key > :last_key ORDER BY key FETCH FIRST :n ROWS ONLY`)
  — every batch is a cheap indexed range scan. Cost per batch stays flat even
  on the billionth row, unlike `OFFSET`, and there is no long-lived open
  cursor to trigger `ORA-01555` on a table that takes hours to process.
- **Configurable pacing** — `BATCH_SIZE` rows per cycle, then `SLEEP_SECONDS`
  of idle time.
- **One commit per batch** — every row in a batch is applied and committed
  together; the bookkeeping writes (history, processed marks, quarantine) are
  still array-bound, so they cost one round trip each.

## Reliability

- **Checkpointing** — after every committed batch the last change key is
  written (atomically) to `.sync_checkpoint.json`. Kill the program at any
  point and rerun it: it resumes where it stopped. `--restart` starts over.
- **Idempotent replay** — write your SQL to be safe to repeat (the shipped
  update/delete are no-ops when the row is absent), so the crash window between
  commit and checkpoint is harmless.
- **Graceful shutdown** — `Ctrl+C` / `SIGTERM` finishes the current batch,
  saves the checkpoint, and exits cleanly.
- **Auto-reconnect** — recoverable connection errors are retried with
  exponential backoff.
- **Bad rows** — a row Oracle rejects is recorded `FAILED` (and its key
  quarantined when enabled); an unknown operation value is logged and skipped —
  neither kills the run.

## Setup

```bash
cp .env.example .env        # edit credentials, table names, pacing
dotnet restore              # pull NuGet dependencies
```

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

To try it end-to-end, `sql/example_tables.sql` creates a demo change-log /
target pair (including an optional seed block with inserts, updates, and
deletes).

Deploying to a machine with no internet access? `scripts/prepare-offline-bundle.ps1`
gathers the full NuGet closure (and optionally a self-contained build + the SDK
installer) into one folder — see [DEPLOY-AIRGAPPED.md](DEPLOY-AIRGAPPED.md).

## Run

```bash
dotnet run                                  # uses .env settings
dotnet run -- --batch-size 500 --sleep 10
dotnet run -- --restart                     # ignore checkpoint, start over
```

`--` separates `dotnet run`'s own options from the program's arguments.

For production, publish a self-contained binary and run that instead:

```bash
dotnet publish -c Release -o out
./out/syncdb --batch-size 1000 --sleep 5
```

Sample output:

```
2026-06-11 10:02:11 info  syncdb.sync: Connected to dbhost:1521/ORCLPDB1 as app_user
2026-06-11 10:02:12 info  syncdb.sync: Batch 1: read 1000 changes -> 946 inserts, 22 updates, 32 deletes, 0 failed (total 1000, last CHANGE_ID=1000)
2026-06-11 10:02:17 info  syncdb.sync: Batch 2: read 1000 changes -> 961 inserts, 25 updates, 14 deletes, 0 failed (total 2000, last CHANGE_ID=2000)
...
2026-06-11 11:40:03 info  syncdb.sync: Done. 1500000 change rows applied in 1500 batches (5872.0s).
```

## Adapting to your tables

1. **Source & key** — in `.env`, set `SOURCE_TABLE` (may be schema-qualified,
   e.g. `APPDATA.ORDER_CHANGES`), `KEY_COLUMN` (the change log's unique, indexed,
   ascending column — it drives paging and resume), `OP_COLUMN` / `OP_INSERT` /
   `OP_UPDATE` / `OP_DELETE` to match how your source flags each row, and
   `BUSINESS_KEY_COLUMNS` (the source column(s) identifying a target row, used for
   quarantine + history).
2. **The SQL** — edit `sql/insert.sql`, `sql/update.sql`, `sql/delete.sql` (paths
   in `INSERT_SQL_FILE` / `UPDATE_SQL_FILE` / `DELETE_SQL_FILE`). This is where
   your mapping lives: reference any source column as `:COLUMN_NAME` and do the
   merges/date building/code maps/totals in SQL. Keep each statement idempotent.
   If you link, end the insert with `RETURNING <id> INTO :NEW_ID` and write
   `sql/link.sql` (`LINK_SQL_FILE`) using `:NEW_ID` plus env-var binds.
3. **Pacing** — tune `BATCH_SIZE` / `SLEEP_SECONDS` for your DB. Bigger
   batches finish faster; longer sleeps are gentler. A rough guide: start at
   1000 rows / 5 s and watch your DB's load.

## Configuration reference

| Variable          | Default                 | Meaning                                   |
|-------------------|-------------------------|-------------------------------------------|
| `ORACLE_USER`     | — (required)            | DB username                               |
| `ORACLE_PASSWORD` | — (required)            | DB password                               |
| `ORACLE_DSN`      | — (required)            | `host:port/service_name`                  |
| `BATCH_SIZE`      | `1000`                  | change rows read/applied per cycle        |
| `SLEEP_SECONDS`   | `5`                     | idle time between cycles                  |
| `SOURCE_TABLE`    | `ORDER_CHANGES`         | change-log table to read (`SELECT *`)     |
| `KEY_COLUMN`      | `CHANGE_ID`             | unique, indexed source column for paging  |
| `BUSINESS_KEY_COLUMNS`| `ORDER_ID`          | source key column(s) for quarantine/history |
| `OP_COLUMN`       | `OPERATION`             | source column holding the operation flag  |
| `OP_INSERT`       | `I`                     | flag value meaning insert                 |
| `OP_UPDATE`       | `U`                     | flag value meaning update                 |
| `OP_DELETE`       | `D`                     | flag value meaning delete                 |
| `INSERT_SQL_FILE` | `sql/insert.sql`        | statement run for an insert row           |
| `UPDATE_SQL_FILE` | `sql/update.sql`        | statement run for an update row           |
| `DELETE_SQL_FILE` | `sql/delete.sql`        | statement run for a delete row            |
| `LINK_SQL_FILE`   | — (disabled)            | statement run after each insert to link it|
| `NEW_ID_BIND`     | `NEW_ID`                | bind the insert SQL returns the new id into|
| `SYNC_USER_ID`    | — (env-var bind)        | example value referenced by `link.sql`    |
| `SYNC_USER_SECTION`| — (env-var bind)       | example value referenced by `link.sql`    |
| `CHECKPOINT_FILE` | `.sync_checkpoint.json` | where resume state is stored (keyset mode)|
| `PROCESSED_COLUMN`| — (disabled)            | source queue flag; enables filter + mark  |
| `PROCESSED_PENDING`| `0`                    | flag value meaning "not yet applied"      |
| `PROCESSED_DONE`  | `1`                     | flag value set once a row is applied      |
| `HISTORY_TABLE`   | — (disabled)            | per-row audit trail of every change       |
| `QUARANTINE_TABLE`| — (disabled)            | keys whose insert failed; skips later changes |
| `LOG_LEVEL`       | `INFO`                  | `DEBUG`, `INFO`, `WARNING`, …             |
| `LOG_FILE`        | — (console only)        | also append the program log to this file  |

## Project layout

```
SyncDb.csproj            project file (.NET 10, ODP.NET Core dependency)
src/Program.cs           entry point / CLI
src/SyncConfig.cs        env-based configuration
src/TableSync.cs         batch loop, paging, per-row apply, linking, throttling
src/Checkpoint.cs        atomic resume-state persistence
src/DotEnv.cs            minimal .env loader
src/FileLogger.cs        optional file logging provider (LOG_FILE)
src/RowKey.cs            value-equality business key for the quarantine set
sql/insert.sql           YOUR mapping: statement run for each insert row
sql/update.sql           YOUR mapping: statement run for each update row
sql/delete.sql           YOUR mapping: statement run for each delete row
sql/link.sql             optional: link statement run after each insert
sql/example_tables.sql   demo change-log/target schema + seed data
```
