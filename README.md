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
   `OP_DELETE` to match how your source flags each row.
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
| `CHECKPOINT_FILE` | `.sync_checkpoint.json` | where resume state is stored              |
| `LOG_LEVEL`       | `INFO`                  | `DEBUG`, `INFO`, `WARNING`, …             |

## Project layout

```
SyncDb.csproj            project file (.NET 10, ODP.NET Core dependency)
src/Program.cs           entry point / CLI
src/SyncConfig.cs        env-based configuration
src/TableSync.cs         batch loop, paging, dedup, MERGE/DELETE, throttling
src/Transformer.cs       YOUR mapping: change row -> target row / business key
src/Checkpoint.cs        atomic resume-state persistence
src/DotEnv.cs            minimal .env loader
src/RowKey.cs            value-equality business key for in-batch dedup
sql/example_tables.sql   demo change-log/target schema + seed data
```
