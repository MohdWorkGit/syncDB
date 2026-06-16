# syncDB — System Workflow

> **This document is the behavior spec.** Keep it in sync with the code: whenever
> the sync's runtime behavior changes (new outcome, new failure handling, new
> config that alters flow), update the relevant diagram, the outcome table, and
> the changelog at the bottom in the same change.

This describes what the engine does to **each change-log row** and how it behaves
on **every failure**. Source of truth: [`src/TableSync.cs`](../src/TableSync.cs).

Optional behaviors referenced below are gated by config (see
[`.env.example`](../.env.example) / [README](../README.md)):
`PROCESSED_COLUMN` (queue mode), `LINK_TABLE` (user links), `HISTORY_TABLE`
(audit trail), `QUARANTINE_TABLE` (failed-insert quarantine), `LOG_FILE`.

---

## 1. Top-level batch loop

```
            ┌──────────────────────────────────────────────────────────┐
            │  START: load .env → connect → LoadQuarantine() into memory │
            └───────────────────────────┬──────────────────────────────┘
                                         ▼
        ┌────────────────────────► FETCH BATCH ◄───────────────────────┐
        │                  (queue mode: WHERE processed = pending       │
        │                   AND key NOT IN QUARANTINE_TABLE             │
        │                   ORDER BY key FETCH FIRST :n                 │
        │                   keyset mode: WHERE key > :last_key ...)     │
        │                                │                              │
        │                       rows.Count == 0 ? ──yes──► DONE (exit)  │
        │                                │ no                           │
        │                                ▼                              │
        │                          PLAN BATCH  ─────► classify each row │
        │                                │            (Diagram 2)       │
        │                                ▼                              │
        │                       APPLY BATCH (1 transaction)             │
        │                                │            (Diagram 3)       │
        │           ┌────────────────────┴───────────────────┐         │
        │           │ changes in order → links → quarantine   │         │
        │           │ → history → mark processed → COMMIT     │         │
        │           └────────────────────┬───────────────────┘         │
        │                                ▼                              │
        │                    save checkpoint + log counts              │
        │                                │                              │
        └──────────── sleep N s ◄────────┘ (only if full batch)─────────┘
```

---

## 2. PLAN BATCH — what happens to each change row (every case)

```
  one change-log row
        │
        ▼
  op in {I,U,D}? ───────no──────────────────────────► [SKIPPED_BADROW]
        │ yes                                            (unknown op)
        ▼
  TransformKey ok? ─────no──────────────────────────► [SKIPPED_BADROW]
        │ yes                                            (can't read key)
        ▼
  key already quarantined? ──yes────────────────────► [SKIPPED_QUARANTINED]
        │ no                                             (ignore U/D forever)
        ▼
  needs Transform (I/U)?
        │
        ├─ Transform throws? ──yes──► is it an insert (I)?
        │                                │ yes → [QUARANTINED]  add key to quarantine
        │                                │ no  → [SKIPPED_BADROW]
        │ no (ok, or it's a D)
        ▼
  append to plan, in arrival order (changes are NOT coalesced:
        │           every change to a key survives and is replayed)
        ▼
  survives as ONE PlanItem  →  goes to APPLY (Diagram 3)
```

Every row gets exactly one outcome stamped, and (if `HISTORY_TABLE` is on) one
record in `SYNC_ROW_HISTORY`. In queue mode every read row is stamped processed so
a bad row can never stall the queue head — **except** a row whose business key is
quarantined, which is left **pending** on purpose. Those pending rows are kept out
of the fetch by the `QUARANTINE_TABLE` anti-join, so they don't re-spin the head;
deleting the key from `QUARANTINE_TABLE` (then restarting) requeues them with no
flag reset.

---

## 3. APPLY BATCH — the transaction + failure isolation

```
  BEGIN TRANSACTION
        │
        ▼
  ┌── ApplyOp per run of consecutive same-kind changes, in arrival order ────────────┐
  │   (a new array DML starts whenever the change kind switches upsert<->delete)      │
  │                                                                                   │
  │   SAVEPOINT ──► try ONE bulk array MERGE/DELETE                                   │
  │                      │                                                            │
  │            ┌─────────┴───────────┐                                               │
  │         success                fails                                             │
  │            │                     │                                               │
  │   mark all UPSERTED/        OracleException                                       │
  │     DELETED                      │                                               │
  │            │          ┌──────────┴───────────┐                                   │
  │            │      recoverable?            not recoverable                        │
  │            │      (connection)            (constraint/datatype)                  │
  │            │          │                       │                                  │
  │            │      rethrow ──────┐      ROLLBACK TO SAVEPOINT                      │
  │            │     (whole batch   │             │                                  │
  │            │      retried by    │      replay ROW BY ROW:                        │
  │            │      WithRetries)  │        each row → ok? → UPSERTED/DELETED        │
  │            │                    │                 → fail (non-recov)?             │
  │            │                    │                     → [FAILED]                  │
  │            │                    │                     → if INSERT: quarantine key │
  │            │                    │                     → fail (recov)? → rethrow   │
  │            ▼                    │                                                 │
  └────────────────────────────────┼─────────────────────────────────────────────────┘
        │                          │
        ▼                          │
  links (+row per landed INSERT,   │   ◄─── a recoverable error anywhere bubbles up,
   -rows per landed DELETE)        │        rolls back the WHOLE tx, reconnects, and
        ▼                          │        retries the entire batch (idempotent)
  persist new quarantine keys      │
        ▼                          │
  write history (every row)        │
        ▼                          │
  mark source rows processed       │   ◄─── EXCEPT rows whose key is quarantined:
   (queue mode)                    │        left pending so deleting the key from
        ▼                          │        QUARANTINE_TABLE requeues them
     COMMIT  ───────────────────── all-or-nothing
        │
   (on any uncaught error: ROLLBACK whole tx)
```

> Note: when `QUARANTINE_TABLE` is **not** set, `ApplyOp` does a single bulk array
> DML with no isolation — a data failure aborts the batch (and, if non-recoverable,
> the run), which is the original behavior.
>
> **Link maintenance is isolated the same way** (`ApplyLinks`): each landed insert
> adds a link row and each landed delete removes that key's link rows, applied in
> arrival order (consecutive same-kind ops batched). Each run does a bulk DML first,
> then rollback-to-savepoint and row-by-row on a data failure. A link op that still
> fails is recorded as `LINK_FAILED` and skipped — the already-applied target change
> is **kept** and the run does **not** abort. Recoverable errors still retry the batch.
>
> A link **insert** row carries its own id (`LINK_ID_COLUMN`), the target business
> key, the fixed `SYNC_USER_ID`, and the fixed `SYNC_USER_SECTION`. The id is filled
> by the `MERGE` as `MAX(id) + 1`; because array binding runs the `MERGE` once per
> row in order in the one transaction, each row's `MAX(id)` sees the earlier rows and
> the ids stay sequential and collision-free (single-writer assumption).

---

## 4. Outcome per case (quick reference)

| Case | Outcome | Target | Link table | Quarantine | History |
|------|---------|--------|-----------|-----------|---------|
| `I` valid | `UPSERTED` | row inserted | + link row | — | UPSERTED |
| `U` valid (row exists) | `UPSERTED` | row updated | — | — | UPSERTED |
| `U` valid (row missing) | `UPSERTED` | row inserted via upsert | — (not an `I`) | — | UPSERTED |
| `D` valid | `DELETED` | row removed | link rows removed | — | DELETED |
| several changes, same key, same batch | each applied | all applied in order | `I` adds, `D` removes (in order) | — | every row logged |
| Unknown op / bad key | `SKIPPED_BADROW` | untouched | — | — | SKIPPED_BADROW |
| `I` rejected by transformer | `QUARANTINED` | untouched | — | **key added** | QUARANTINED |
| `I` rejected by Oracle (constraint) | `FAILED` | untouched | — | **key added** | FAILED |
| `U`/`D` rejected by Oracle | `FAILED` | untouched | — | — (not an insert) | FAILED |
| `I`/`D` ok but link op fails | `LINK_FAILED` | target change kept | link row not added/removed | — | LINK_FAILED |
| any change on quarantined key | `SKIPPED_QUARANTINED` | untouched | — | already there | SKIPPED_QUARANTINED |
| connection drop mid-batch | (retried) | nothing until success | — | — | written on the successful attempt |

**Failure principle:** a *recoverable* error (connection) rolls back and retries
the **whole batch**; a *non-recoverable* error (bad data) is isolated to the
**single row**, marked `FAILED`/`QUARANTINED` while the rest of the batch commits.
A quarantined key is skipped forever until you delete it from `SYNC_QUARANTINE`
(and reset its source rows to pending).

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
  (Needed because the requeue change above lets the same `CHANGE_ID` be processed more
  than once; before, each was processed at most once so insert-only was sufficient.)
- **2026-06-15** — Queue mode no longer stamps a quarantined key's change rows as
  processed: they are left **pending** and excluded from the fetch by an anti-join
  on `QUARANTINE_TABLE`, so they don't re-spin the queue head. Removing the key from
  `QUARANTINE_TABLE` (then restarting) requeues them with no processed-flag reset —
  re-running quarantined rows is now a one-table change. (Non-quarantine skips —
  unknown op, unreadable key — are still stamped done. Assumes the change log
  exposes the target key column(s) under the same name as `QUARANTINE_TABLE`.)
- **2026-06-15** — Link insert rows now carry their own id and a user section. The
  id column (`LINK_ID_COLUMN`) is filled by the `MERGE` as `MAX(id)+1`; the section
  (`LINK_SECTION_COLUMN`) is the fixed `SYNC_USER_SECTION`, alongside the existing
  fixed `SYNC_USER_ID`. Both `SYNC_USER_ID` and `SYNC_USER_SECTION` are now required
  when `LINK_TABLE` is set. (Transformer-only, no flow change: the example now merges
  three text date parts — day/month/year — into one `ORDER_DATE` instead of splitting
  a date, and documents the code-map pattern for `STATUS_CODE`/`COUNTRY_CODE`.)
