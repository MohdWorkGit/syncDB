# syncDB

Throttled, resumable copy of a **very large Oracle table** into another table
with a **different structure**, without hammering the database.

The script works in cycles: read a batch of rows → transform them into the
target structure → insert + commit → **sleep** → repeat until the source is
exhausted. Between cycles the database is completely idle, so a multi-hour
sync only ever produces short, light bursts of load.

```
┌────────────────┐   batch    ┌─────────────┐   executemany   ┌──────────────┐
│  SOURCE table   │ ─────────▶ │  transform  │ ──────────────▶ │ TARGET table │
│ (huge, legacy)  │  keyset    │  (Python)   │   + commit      │ (new shape)  │
└────────────────┘  paging     └─────────────┘                 └──────────────┘
        ▲                                                            │
        └──────────────── sleep N seconds, repeat ◀──────────────────┘
```

## Why it stays light on the DB

- **Keyset pagination** (`WHERE key > :last_key ORDER BY key FETCH FIRST :n ROWS ONLY`)
  — every batch is a cheap indexed range scan. Cost per batch stays flat even
  on the billionth row, unlike `OFFSET`, and there is no long-lived open
  cursor to trigger `ORA-01555` on a table that takes hours to copy.
- **Configurable pacing** — `BATCH_SIZE` rows per cycle, then `SLEEP_SECONDS`
  of idle time.
- **Array inserts** — each batch is written with a single `executemany()`
  round trip and one commit.

## Reliability

- **Checkpointing** — after every committed batch the last key is written
  (atomically) to `.sync_checkpoint.json`. Kill the script at any point and
  rerun it: it resumes where it stopped. `--restart` starts over.
- **Idempotent resume** — rows re-inserted after a crash are detected via the
  target's primary key (`ORA-00001`) and skipped; everything else in the
  batch still goes in (`batcherrors=True`).
- **Graceful shutdown** — `Ctrl+C` / `SIGTERM` finishes the current batch,
  saves the checkpoint, and exits cleanly.
- **Auto-reconnect** — recoverable connection errors are retried with
  exponential backoff.
- **Bad rows** — a row the transformer rejects (`ValueError`) is logged and
  skipped instead of killing the run.

## Setup

```bash
pip install -r requirements.txt
cp .env.example .env        # edit credentials, table names, pacing
```

Uses [python-oracledb](https://python-oracledb.readthedocs.io/) in thin mode —
no Oracle Instant Client installation needed.

To try it end-to-end, `sql/example_tables.sql` creates a demo source/target
pair (including an optional 100k-row seed block).

## Run

```bash
python run_sync.py                          # uses .env settings
python run_sync.py --batch-size 500 --sleep 10
python run_sync.py --restart                # ignore checkpoint, start over
```

Sample output:

```
2026-06-11 10:02:11 INFO  syncdb.sync: Connected to dbhost:1521/ORCLPDB1 as app_user
2026-06-11 10:02:12 INFO  syncdb.sync: Batch 1: read 1000, inserted 1000 (total 1000, last ORDER_ID=1000)
2026-06-11 10:02:17 INFO  syncdb.sync: Batch 2: read 1000, inserted 1000 (total 2000, last ORDER_ID=2000)
...
2026-06-11 11:40:03 INFO  syncdb.sync: Done. 1500000 rows copied in 1500 batches (5872.0s).
```

## Adapting to your tables

1. **Tables & key** — set `SOURCE_TABLE`, `TARGET_TABLE`, and `KEY_COLUMN` in
   `.env`. The key column must be unique and indexed on the source (a primary
   key is perfect); it is what makes batch reads cheap and resume possible.
2. **Columns & mapping** — edit `syncdb/transformer.py`:
   - `SOURCE_COLUMNS`: what to read,
   - `TARGET_COLUMNS`: what to write,
   - `transform(row)`: how one source row becomes one target tuple
     (merge fields, split dates, decode codes, compute totals, …).
   Raise `ValueError` inside `transform()` to skip a bad row.
3. **Pacing** — tune `BATCH_SIZE` / `SLEEP_SECONDS` for your DB. Bigger
   batches finish faster; longer sleeps are gentler. A rough guide: start at
   1000 rows / 5 s and watch your DB's load.

## Configuration reference

| Variable          | Default                 | Meaning                                  |
|-------------------|-------------------------|------------------------------------------|
| `ORACLE_USER`     | — (required)            | DB username                              |
| `ORACLE_PASSWORD` | — (required)            | DB password                              |
| `ORACLE_DSN`      | — (required)            | `host:port/service_name`                 |
| `BATCH_SIZE`      | `1000`                  | rows read/inserted per cycle             |
| `SLEEP_SECONDS`   | `5`                     | idle time between cycles                 |
| `SOURCE_TABLE`    | `SOURCE_ORDERS`         | table to read                            |
| `TARGET_TABLE`    | `ORDER_SUMMARY`         | table to write                           |
| `KEY_COLUMN`      | `ORDER_ID`              | unique, indexed source column for paging |
| `CHECKPOINT_FILE` | `.sync_checkpoint.json` | where resume state is stored             |
| `LOG_LEVEL`       | `INFO`                  | `DEBUG`, `INFO`, `WARNING`, …            |

## Project layout

```
run_sync.py              entry point / CLI
syncdb/config.py         env-based configuration
syncdb/sync.py           batch loop, paging, throttling, retries
syncdb/transformer.py    YOUR mapping: source row -> target row
syncdb/checkpoint.py     atomic resume-state persistence
sql/example_tables.sql   demo source/target schema + seed data
```
