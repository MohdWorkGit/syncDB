# syncDB

Throttled, resumable apply of a **very large Oracle change-log table** onto a
target table with a **different structure**, without hammering the database.

Each source row carries an **operation column** — insert (`I`), update (`U`),
or delete (`D`) — that says what to do to the target. The script works in
cycles: read a batch of change rows → transform them into the target
structure → apply (MERGE / DELETE) + commit → **sleep** → repeat until the
source is exhausted. Between cycles the database is completely idle, so a
multi-hour sync only ever produces short, light bursts of load.

```
┌─────────────────┐   batch    ┌─────────────┐  MERGE (I/U)    ┌──────────────┐
│ CHANGE LOG table │ ─────────▶ │  transform  │ ──────────────▶ │ TARGET table │
│ (huge: I/U/D     │  keyset    │  + dedup    │  DELETE (D)     │ (new shape)  │
│  per row)        │  paging    │  (Python)   │  + commit       │              │
└─────────────────┘            └─────────────┘                 └──────────────┘
        ▲                                                            │
        └──────────────── sleep N seconds, repeat ◀──────────────────┘
```

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
to one `executemany()` MERGE and one `executemany()` DELETE — two round
trips and one commit.

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
- **Array DML** — one `executemany()` per operation type per batch, one
  commit per batch.

## Reliability

- **Checkpointing** — after every committed batch the last change key is
  written (atomically) to `.sync_checkpoint.json`. Kill the script at any
  point and rerun it: it resumes where it stopped. `--restart` starts over.
- **Idempotent replay** — MERGE upserts and key-based deletes are safe to
  repeat, so the crash window between commit and checkpoint is harmless.
- **Graceful shutdown** — `Ctrl+C` / `SIGTERM` finishes the current batch,
  saves the checkpoint, and exits cleanly.
- **Auto-reconnect** — recoverable connection errors are retried with
  exponential backoff.
- **Bad rows** — a row the transformer rejects (`ValueError`) or with an
  unknown operation value is logged and skipped instead of killing the run.

## Setup

```bash
pip install -r requirements.txt
cp .env.example .env        # edit credentials, table names, pacing
```

Uses [python-oracledb](https://python-oracledb.readthedocs.io/) in thin mode —
no Oracle Instant Client installation needed.

To try it end-to-end, `sql/example_tables.sql` creates a demo change-log /
target pair (including an optional seed block with inserts, updates, and
deletes).

## Run

```bash
python run_sync.py                          # uses .env settings
python run_sync.py --batch-size 500 --sleep 10
python run_sync.py --restart                # ignore checkpoint, start over
```

Sample output:

```
2026-06-11 10:02:11 INFO  syncdb.sync: Connected to dbhost:1521/ORCLPDB1 as app_user
2026-06-11 10:02:12 INFO  syncdb.sync: Batch 1: read 1000 changes -> 968 upserts, 22 deletes (total 1000, last CHANGE_ID=1000)
2026-06-11 10:02:17 INFO  syncdb.sync: Batch 2: read 1000 changes -> 975 upserts, 14 deletes (total 2000, last CHANGE_ID=2000)
...
2026-06-11 11:40:03 INFO  syncdb.sync: Done. 1500000 change rows applied in 1500 batches (5872.0s).
```

## Adapting to your tables

1. **Tables & columns** — in `.env`, set `SOURCE_TABLE`, `TARGET_TABLE`,
   `KEY_COLUMN` (the change log's unique, indexed, ascending column — it
   drives paging and resume), and `OP_COLUMN` / `OP_INSERT` / `OP_UPDATE` /
   `OP_DELETE` to match how your source flags each row.
2. **Mapping** — edit `syncdb/transformer.py`:
   - `SOURCE_COLUMNS`: what to read (must include the key and op columns),
   - `TARGET_COLUMNS`: what to write,
   - `TARGET_KEY_COLUMNS`: which target column(s) identify a row — used to
     match rows for MERGE and DELETE,
   - `transform(row)`: how one insert/update row becomes one target tuple
     (merge fields, split dates, decode codes, compute totals, …),
   - `transform_key(row)`: how to extract the business key (all that delete
     rows need — their other columns are usually NULL).
   Raise `ValueError` inside either function to skip a bad row.
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
run_sync.py              entry point / CLI
syncdb/config.py         env-based configuration
syncdb/sync.py           batch loop, paging, dedup, MERGE/DELETE, throttling
syncdb/transformer.py    YOUR mapping: change row -> target row / business key
syncdb/checkpoint.py     atomic resume-state persistence
sql/example_tables.sql   demo change-log/target schema + seed data
```
