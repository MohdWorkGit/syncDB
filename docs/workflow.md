# syncDB — System Workflow

> **This document is the behavior spec.** Keep it in sync with the code: whenever
> the sync's runtime behavior changes (new outcome, new failure handling, new
> config that alters flow), update the relevant diagram, the outcome table, and
> the changelog at the bottom in the same change.

This describes what the engine does to **each change-log row** and how it behaves
on **every failure**. Source of truth: [`src/TableSync.cs`](../src/TableSync.cs).

The engine is a thin driver: it reads the source in batches and, for each row,
runs the **hand-written SQL statement** for that row's operation — one editable
file each for insert / update / delete (`INSERT_SQL_FILE` / `UPDATE_SQL_FILE` /
`DELETE_SQL_FILE`). Each `:NAME` placeholder is bound from the source row by
column name; the source→target transformation lives entirely in those files.

Optional behaviors referenced below are gated by config (see
[`.env.example`](../.env.example) / [README](../README.md)):
`PROCESSED_COLUMN` (queue mode), `LINK_SQL_FILE` (post-insert user links),
`HISTORY_TABLE` (audit trail), `QUARANTINE_TABLE` (failed-insert quarantine),
`LOG_FILE`.

---

## 1. Top-level batch loop

```
            ┌──────────────────────────────────────────────────────────┐
            │  START: load .env + SQL files → connect → LoadQuarantine() │
            └───────────────────────────┬──────────────────────────────┘
                                         ▼
        ┌────────────────────────► FETCH BATCH ◄───────────────────────┐
        │                  SELECT * FROM source                         │
        │                  (queue mode: WHERE processed = pending       │
        │                   AND key NOT IN QUARANTINE_TABLE             │
        │                   ORDER BY key FETCH FIRST :n                 │
        │                   keyset mode: WHERE key > :last_key ...)     │
        │                                │                              │
        │                       rows.Count == 0 ? ──yes──► DONE (exit)  │
        │                                │ no                           │
        │                                ▼                              │
        │                       APPLY BATCH (1 transaction)             │
        │                                │            (Diagrams 2 + 3)  │
        │           ┌────────────────────┴───────────────────┐         │
        │           │ each row in order (pick SQL by op,      │         │
        │           │  bind, run, link inserts) → quarantine  │         │
        │           │  → history → mark processed → COMMIT    │         │
        │           └────────────────────┬───────────────────┘         │
        │                                ▼                              │
        │                    save checkpoint + log counts              │
        │                                │                              │
        └──────────── sleep N s ◄────────┘ (only if full batch)─────────┘
```

---

## 2. Per-row classification — which SQL runs (every case)

```
  one change-log row
        │
        ▼
  op == OP_INSERT / OP_UPDATE / OP_DELETE? ──no──► [SKIPPED_BADROW]
        │ yes (pick insert/update/delete SQL)        (unknown op)
        ▼
  read business key (BUSINESS_KEY_COLUMNS)
        │
        ▼
  key already quarantined? ──yes────────────────► [SKIPPED_QUARANTINED]
        │ no                                         (ignore every change forever)
        ▼
  go to APPLY (Diagram 3): run that op's SQL, bind :NAME from the row
```

Every row gets exactly one outcome stamped, and (if `HISTORY_TABLE` is on) one
record in `SYNC_ROW_HISTORY`. In queue mode every read row is stamped processed so
a bad row can never stall the queue head — **except** a row whose business key is
quarantined, which is left **pending** on purpose. Those pending rows are kept out
of the fetch by the `QUARANTINE_TABLE` anti-join, so they don't re-spin the head;
deleting the key from `QUARANTINE_TABLE` (then restarting) requeues them with no
flag reset.

---

## 3. APPLY BATCH — per-row apply + failure isolation

```
  BEGIN TRANSACTION
        │
        ▼
  ┌── for each row, in arrival order ─────────────────────────────────────────────┐
  │                                                                                │
  │   SAVEPOINT syncdb_row ──► run this op's SQL, binds from the row               │
  │                      │                                                         │
  │            ┌─────────┴───────────┐                                            │
  │         success                fails                                          │
  │            │                     │                                            │
  │   mark INSERTED/            OracleException                                    │
  │     UPDATED/DELETED              │                                            │
  │            │          ┌──────────┴───────────┐                                │
  │            │      recoverable?            not recoverable                     │
  │            │      (connection)            (constraint/datatype/bad date)      │
  │            │          │                       │                               │
  │            │      rethrow ──────┐      ROLLBACK TO SAVEPOINT                   │
  │            │     (whole batch   │             │                               │
  │            │      retried by    │       mark [FAILED]                         │
  │            │      WithRetries)  │       if INSERT & quarantine on:            │
  │            │                    │          add key to quarantine              │
  │            ▼                    │                                             │
  │   was it an INSERT and LINK_SQL_FILE set?                                     │
  │            │ yes                │                                             │
  │            ▼                    │                                             │
  │   SAVEPOINT syncdb_link ──► run link SQL (:NEW_ID = the returned id +         │
  │            │                   env-var binds; nothing from the source row)    │
  │       ┌────┴────┐              │                                             │
  │    success    fails (non-recov) → ROLLBACK TO SAVEPOINT → mark [LINK_FAILED]  │
  │       │         (insert kept)   │  (recoverable → rethrow, retry batch)       │
  └───────┴─────────────────────────┼──────────────────────────────────────────────┘
        │                          │
        ▼                          │   ◄─── a recoverable error anywhere bubbles up,
  persist new quarantine keys      │        rolls back the WHOLE tx, reconnects, and
        ▼                          │        retries the entire batch
  write history (every row)        │
        ▼                          │
  mark source rows processed       │   ◄─── EXCEPT rows whose key is quarantined:
   (queue mode)                    │        left pending so deleting the key from
        ▼                          │        QUARANTINE_TABLE requeues them
     COMMIT  ───────────────────── all-or-nothing
        │
   (on any uncaught error: ROLLBACK whole tx)
```

> **Isolation:** each data row runs on its own savepoint, so a non-recoverable
> failure is isolated to that one row (recorded `FAILED`) and the rest of the
> batch still commits. There is no bulk-then-row-by-row retry — rows are applied
> one at a time to begin with.
>
> **Linking is post-insert only.** After a successful insert (when `LINK_SQL_FILE`
> is set) the link SQL runs once, on its own savepoint, with `:NEW_ID` = the id the
> insert returned via `RETURNING <id> INTO :NEW_ID` plus any env-var binds. Nothing
> in the link row comes from the source row. A link failure is recorded
> `LINK_FAILED` and the insert is **kept**. A target **delete** does not unlink
> anything in the engine — cleanup belongs in your `delete.sql` / schema (the
> example uses an `ON DELETE CASCADE` foreign key).
>
> The history and processed-mark writes are still array-bound (one round trip
> each) — only the data apply is row-at-a-time.

---

## 4. Outcome per case (quick reference)

| Case | Outcome | Target | Link table | Quarantine | History |
|------|---------|--------|-----------|-----------|---------|
| `I` ok | `INSERTED` | row inserted | + link row (post-insert) | — | INSERTED |
| `U` ok | `UPDATED` | row updated (0 rows if absent) | — | — | UPDATED |
| `D` ok | `DELETED` | row removed | link rows removed by `ON DELETE CASCADE` | — | DELETED |
| Unknown op | `SKIPPED_BADROW` | untouched | — | — | SKIPPED_BADROW |
| `I` rejected by Oracle (constraint / bad date) | `FAILED` | untouched | — | **key added** | FAILED |
| `U`/`D` rejected by Oracle | `FAILED` | untouched | — | — (not an insert) | FAILED |
| `I` ok but link SQL fails | `LINK_FAILED` | row kept | link row not added | — | LINK_FAILED |
| any change on quarantined key | `SKIPPED_QUARANTINED` | untouched | — | already there | SKIPPED_QUARANTINED |
| connection drop mid-batch | (retried) | nothing until success | — | — | written on the successful attempt |

**Failure principle:** a *recoverable* error (connection) rolls back and retries
the **whole batch**; a *non-recoverable* error (bad data) is isolated to the
**single row**, marked `FAILED` while the rest of the batch commits. A failed
insert also quarantines its business key (when `QUARANTINE_TABLE` is set), so it
is skipped forever until you delete it from the quarantine table (and, in keyset
mode, reset/recreate its source rows).

---

## Changelog

Record every behavior-affecting change here.

- **2026-06-14** — Initial workflow spec: queue mode, user links (inserts only),
  per-row history, failed-insert quarantine with row-by-row isolation, log file.
- **2026-06-14** — Link inserts now isolated like data (`ApplyLinks`): a failed
  link is recorded as `LINK_FAILED` and skipped instead of aborting the whole run;
  the upserted target row is kept.
- **2026-06-14** — Dropped per-key coalescing/dedup: every change in a batch is now
  applied in arrival order (no more `SUPERSEDED`). Apply batches a run of consecutive
  same-kind changes and starts a new array DML when the kind switches, so a key's
  full change sequence is replayed correctly. Links are now keyed off each `I`
  directly rather than a per-key "insert seen" flag.
- **2026-06-14** — A landed `D` now also removes that business key's link rows
  (`_linkDeleteSql`). Link maintenance runs in arrival order (insert adds, delete
  removes, consecutive same-kind batched) so same-batch insert/delete sequences end
  in the correct link state; a failed link delete is `LINK_FAILED` like a failed
  link insert.
- **2026-06-15** — History (`SYNC_ROW_HISTORY`) MERGE now also updates on match, not
  just inserts on no-match. A change id that is **re-processed** (a `QUARANTINED` row
  that requeues after its key is cleared, then applies) refreshes its history row to
  the new outcome/detail/`RECORDED_AT` instead of keeping the stale first outcome.
- **2026-06-15** — Queue mode no longer stamps a quarantined key's change rows as
  processed: they are left **pending** and excluded from the fetch by an anti-join
  on `QUARANTINE_TABLE`, so they don't re-spin the queue head.
- **2026-06-15** — Link insert rows now carry their own id and a user section.
- **2026-06-17** — Link table's business-key column(s) made configurable via
  `LINK_KEY_COLUMNS`.
- **2026-06-17** — **Major simplification.** The engine no longer generates the
  apply SQL from column metadata. Instead it runs a **hand-written statement per
  operation** (`INSERT_SQL_FILE` / `UPDATE_SQL_FILE` / `DELETE_SQL_FILE`), binding
  each `:NAME` from the source row by column name (`SELECT *` makes every column
  bindable). The C# transformer (`src/Transformer.cs`) is **removed** — all
  source→target transformation now lives in those SQL files. Consequences:
  - Rows are applied **one at a time, each on its own savepoint** (no array-bound
    `MERGE`/`DELETE`, no bulk-then-row-by-row isolation). History / processed-mark
    / quarantine writes stay array-bound.
  - Outcomes are now `INSERTED` / `UPDATED` / `DELETED` (replacing the merged
    `UPSERTED`); the transformer-reject `QUARANTINED` outcome is gone — a bad insert
    surfaces as `FAILED` (which still quarantines the key when enabled).
  - **Linking is post-insert only**: after a successful insert, `LINK_SQL_FILE`
    runs with `:NEW_ID` (captured from the insert's `RETURNING ... INTO :NEW_ID`)
    plus env-var binds — nothing from the source row, no `MAX(id)+1`. Link-on-delete
    is removed; deletes clean up links via the schema (`ON DELETE CASCADE`).
  - Config: dropped `TARGET_TABLE` and all `LINK_*` column settings; added
    `INSERT_SQL_FILE` / `UPDATE_SQL_FILE` / `DELETE_SQL_FILE` / `LINK_SQL_FILE`,
    `NEW_ID_BIND`, and `BUSINESS_KEY_COLUMNS` (source key for quarantine/history).
    The example `ORDER_SUMMARY` gains an identity `SUMMARY_ID` so the insert has an
    id to return and the link table references it.
