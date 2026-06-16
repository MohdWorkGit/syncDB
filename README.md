# syncDB

Throttled, resumable apply of a **very large Oracle change-log table** onto a
target table with a **different structure**, without hammering the database.

Each source row carries an **operation column** — insert (`I`), update (`U`),
or delete (`D`) — that says what to do to the target. The program works in
cycles: read a batch of change rows → transform them into the target
structure → apply (MERGE / DELETE) + commit → **sleep** → repeat until the
source is exhausted. Between cycles the database is completely idle, so a
multi-hour sync only ever produces short, light bursts of load.

```
┌─────────────────┐   batch    ┌─────────────┐  MERGE (I/U)    ┌──────────────┐
│ CHANGE LOG table │ ─────────▶ │  transform  │ ──────────────▶ │ TARGET table │
│ (huge: I/U/D     │  keyset    │  + dedup    │  DELETE (D)     │ (new shape)  │
│  per row)        │  paging    │   (C#)      │  + commit       │              │
└─────────────────┘            └─────────────┘                 └──────────────┘
        ▲                                                            │
        └──────────────── sleep N seconds, repeat ◀──────────────────┘
```

Built on **.NET 10** (C# 14) and [ODP.NET Core](https://www.nuget.org/packages/Oracle.ManagedDataAccess.Core)
in fully managed mode — no Oracle Instant Client installation needed.

See [docs/workflow.md](docs/workflow.md) for the full per-row workflow and
failure-handling diagrams.

## How operations are applied

| `OPERATION` value | Action on target                                      |
|-------------------|-------------------------------------------------------|
| `I` (insert)      | `MERGE` — inserts the transformed row                 |
| `U` (update)      | `MERGE` — updates the existing row (inserts if absent)|
| `D` (delete)      | `DELETE` by business key                              |

Inserts and updates both go through `MERGE` (upsert), which makes the apply
**idempotent**: replaying a batch after a crash, or receiving a `U` whose `I`
was already applied, can never fail or duplicate data.

Within a batch, multiple changes to the same target row are deduplicated
keeping the **latest** one (changes are read in key order): `I` then `D`
nets out to a delete, `D` then `I` to an upsert. That keeps each batch down
to one array-bound MERGE and one array-bound DELETE — two round trips and one
commit.

The operation column name and its three values are configurable
(`OP_COLUMN`, `OP_INSERT`, `OP_UPDATE`, `OP_DELETE`); rows with any other
value are logged and skipped.

## Linking inserted rows to a user

Set `LINK_TABLE` and, for each change row with operation `I`, the engine also
writes one row into that table connecting the inserted target row to a user. Each
link row carries its **own id**, the target business key, the fixed `SYNC_USER_ID`,
and the fixed `SYNC_USER_SECTION`. The link write is an array-bound `MERGE` in the
**same transaction** as the main apply, so it commits atomically with the insert
and is idempotent on replay — a crash-replayed batch never duplicates a link.

The link row's own id (`LINK_ID_COLUMN`, default `LINK_ID`) is set by the database
to `MAX(id) + 1`: the `MERGE` reads the current maximum and adds one. Array binding
runs the `MERGE` once per row in order within the one transaction, so each row's
`MAX(id)` sees the rows inserted just before it and the ids come out sequential and
collision-free. (This assumes a single sync writer; it is not safe for two
processes inserting links into the same table concurrently.)

Only inserts are linked: a key that is updated (`U`) or deleted (`D`) produces
no link, and an insert that is itself deleted within the same batch is skipped.
The link table is expected to hold its own id column (`LINK_ID_COLUMN`), the
target business key column(s) (`TargetKeyColumns`, e.g. `ORDER_ID`), the user
column (`LINK_USER_COLUMN`, default `USER_ID`), and the section column
(`LINK_SECTION_COLUMN`, default `USER_SECTION`). Leave `LINK_TABLE` blank to disable.

A link insert that fails on data (bad `SYNC_USER_ID`, missing table, constraint)
never aborts the run: it is isolated row by row, recorded as `LINK_FAILED`, and
skipped — the target row stays upserted. Only recoverable connection errors retry
the whole batch.

## Consuming the source as a queue (processed flag)

Set `PROCESSED_COLUMN` and the source behaves like a work queue instead of using
keyset paging:

- each batch reads only rows whose flag equals `PROCESSED_PENDING`
  (`WHERE processed = :pending ORDER BY key FETCH FIRST :n`);
- after applying, **every** row read this batch is stamped `PROCESSED_DONE` in
  the *same transaction* — so a row is marked done only if its effect committed,
  and replay is safe.

Rows that are skipped for a non-recoverable reason (bad transform, unknown
operation, unreadable key) are stamped done too, so a poison row at the head of
the queue can never stall progress. The **one exception** is a row whose business
key is quarantined: it is left pending on purpose so it can be requeued just by
clearing the key (see below). Those pending rows are excluded from the fetch by an
anti-join on `QUARANTINE_TABLE`, so they never re-spin the queue head. Because the
flag itself records progress, resume is automatic and the checkpoint file is no
longer needed for correctness. Index the source on `(processed, key)` so each
batch stays a cheap range scan. Leave `PROCESSED_COLUMN` blank to keep the
keyset + checkpoint behavior.

## Failed inserts: quarantine + per-row history

A change row's insert can fail two ways: the transformer rejects it
(`TransformException`), or Oracle rejects the DML (constraint, datatype, …). With
`QUARANTINE_TABLE` set, neither stops the run:

- the failure is **isolated** — the batch's bulk `MERGE`/`DELETE` is tried first,
  and only if it fails on data is it retried row by row to find the offender, so
  the rest of the batch still applies (recoverable *connection* errors still bubble
  up and retry the whole batch);
- the offending business key is **quarantined** — written to `QUARANTINE_TABLE`
  and, from then on, **every later change for that key (update or delete) is
  skipped**. The quarantine set is loaded into memory at startup, so it survives
  restarts. Without it, the `U` that follows a failed `I` would otherwise *recreate*
  the row via the upsert `MERGE`.

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
| `UPSERTED` / `DELETED`   | applied to the target |
| `SUPERSEDED`             | coalesced by a later change to the same key in the batch |
| `SKIPPED_BADROW`         | unknown operation or rejected transform (non-insert) |
| `QUARANTINED`            | this insert failed and quarantined the key |
| `SKIPPED_QUARANTINED`    | the key was already quarantined |
| `FAILED`                 | Oracle rejected the apply |
| `LINK_FAILED`            | row upserted, but its user-link insert failed (link skipped) |

The history write is part of the same transaction as the apply and is keyed on
`CHANGE_ID`, so it commits atomically with the change and is idempotent on replay.
If the **same** change id is later *re-processed* — e.g. a `QUARANTINED` change that
requeues after its key is cleared and now applies — the history row is **updated** in
place to the new outcome and timestamp, so it always reflects the latest run rather
than the stale first attempt. Trace one row's life with
`SELECT * FROM SYNC_ROW_HISTORY WHERE order_id = :id ORDER BY change_id`.

Set `LOG_FILE` to also append the program log to a file alongside the console.

## Why it stays light on the DB

- **Keyset pagination** (`WHERE key > :last_key ORDER BY key FETCH FIRST :n ROWS ONLY`)
  — every batch is a cheap indexed range scan. Cost per batch stays flat even
  on the billionth row, unlike `OFFSET`, and there is no long-lived open
  cursor to trigger `ORA-01555` on a table that takes hours to process.
- **Configurable pacing** — `BATCH_SIZE` rows per cycle, then `SLEEP_SECONDS`
  of idle time.
- **Array DML** — one array-bound command per operation type per batch, one
  commit per batch.

## Reliability

- **Checkpointing** — after every committed batch the last change key is
  written (atomically) to `.sync_checkpoint.json`. Kill the program at any
  point and rerun it: it resumes where it stopped. `--restart` starts over.
- **Idempotent replay** — MERGE upserts and key-based deletes are safe to
  repeat, so the crash window between commit and checkpoint is harmless.
- **Graceful shutdown** — `Ctrl+C` / `SIGTERM` finishes the current batch,
  saves the checkpoint, and exits cleanly.
- **Auto-reconnect** — recoverable connection errors are retried with
  exponential backoff.
- **Bad rows** — a row the transformer rejects (`TransformException`) or with
  an unknown operation value is logged and skipped instead of killing the run.

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
2026-06-11 10:02:12 info  syncdb.sync: Batch 1: read 1000 changes -> 968 upserts, 22 deletes (total 1000, last CHANGE_ID=1000)
2026-06-11 10:02:17 info  syncdb.sync: Batch 2: read 1000 changes -> 975 upserts, 14 deletes (total 2000, last CHANGE_ID=2000)
...
2026-06-11 11:40:03 info  syncdb.sync: Done. 1500000 change rows applied in 1500 batches (5872.0s).
```

## Adapting to your tables

1. **Tables & columns** — in `.env`, set `SOURCE_TABLE`, `TARGET_TABLE`,
   `KEY_COLUMN` (the change log's unique, indexed, ascending column — it
   drives paging and resume), and `OP_COLUMN` / `OP_INSERT` / `OP_UPDATE` /
   `OP_DELETE` to match how your source flags each row. Every table name
   (`SOURCE_TABLE`, `TARGET_TABLE`, `LINK_TABLE`, `HISTORY_TABLE`,
   `QUARANTINE_TABLE`) may be **schema-qualified** — e.g. `APPDATA.ORDER_CHANGES`
   — to read or write tables owned by another schema. Column names cannot be
   qualified.
2. **Mapping** — edit `src/Transformer.cs`:
   - `SourceColumns`: what to read (must include the key and op columns),
   - `TargetColumns`: what to write,
   - `TargetKeyColumns`: which target column(s) identify a row — used to
     match rows for MERGE and DELETE,
   - `Transform(row)`: how one insert/update row becomes one target tuple
     (merge fields, split dates, decode codes, compute totals, …),
   - `TransformKey(row)`: how to extract the business key (all that delete
     rows need — their other columns are usually NULL).
   Throw `TransformException` inside either method to skip a bad row.
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
| `SOURCE_TABLE`    | `ORDER_CHANGES`         | change-log table to read                  |
| `TARGET_TABLE`    | `ORDER_SUMMARY`         | table to maintain                         |
| `KEY_COLUMN`      | `CHANGE_ID`             | unique, indexed source column for paging  |
| `OP_COLUMN`       | `OPERATION`             | source column holding the operation flag  |
| `OP_INSERT`       | `I`                     | flag value meaning insert                 |
| `OP_UPDATE`       | `U`                     | flag value meaning update                 |
| `OP_DELETE`       | `D`                     | flag value meaning delete                 |
| `CHECKPOINT_FILE` | `.sync_checkpoint.json` | where resume state is stored (keyset mode)|
| `LINK_TABLE`      | — (disabled)            | table linking each inserted row to a user |
| `LINK_ID_COLUMN`  | `LINK_ID`               | link table's own id column (set to `MAX(id)+1`) |
| `LINK_USER_COLUMN`| `USER_ID`               | user-id column in `LINK_TABLE`            |
| `LINK_SECTION_COLUMN`| `USER_SECTION`       | section column in `LINK_TABLE`            |
| `SYNC_USER_ID`    | — (req. if linking)     | fixed user id written for each insert      |
| `SYNC_USER_SECTION`| — (req. if linking)    | fixed user section written for each insert |
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
src/TableSync.cs         batch loop, paging, dedup, MERGE/DELETE, throttling
src/Transformer.cs       YOUR mapping: change row -> target row / business key
src/Checkpoint.cs        atomic resume-state persistence
src/DotEnv.cs            minimal .env loader
src/FileLogger.cs        optional file logging provider (LOG_FILE)
src/RowKey.cs            value-equality business key for in-batch dedup
sql/example_tables.sql   demo change-log/target schema + seed data
```
