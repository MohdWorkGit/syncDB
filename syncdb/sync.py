"""Core sync engine: throttled, resumable apply of an Oracle change log.

The source table is a change log: each row carries an operation flag saying
whether the target row should be inserted, updated, or deleted. The engine
applies those changes to the target table in batches.

Strategy
--------
The source is read with *keyset pagination* on its indexed key column
(typically the change log's own sequence):

    SELECT ... WHERE key > :last_key ORDER BY key FETCH FIRST :n ROWS ONLY

Each query touches only the next slice of the index, so the cost per batch is
flat no matter how large the table is — unlike OFFSET pagination, which gets
slower the deeper it goes, and unlike one giant open cursor, which risks
ORA-01555 (snapshot too old) on long runs.

Within a batch, changes are deduplicated per target business key keeping the
*latest* change (rows arrive in key order, so later wins): an insert followed
by a delete nets out to a delete, a delete followed by a re-insert nets out
to an upsert. Inserts and updates are then applied with one MERGE
executemany() and deletes with one DELETE executemany() — both idempotent,
so replaying a batch after a crash is harmless.

After every batch the engine commits, persists a checkpoint, and sleeps, so
the database only ever sees short bursts of light work.
"""

import logging
import re
import signal
import time

import oracledb

from .checkpoint import Checkpoint
from .config import Config
from .transformer import (
    SOURCE_COLUMNS,
    TARGET_COLUMNS,
    TARGET_KEY_COLUMNS,
    transform,
    transform_key,
)

log = logging.getLogger(__name__)

_IDENTIFIER_RE = re.compile(r"^[A-Za-z][A-Za-z0-9_$#]*$")

# How many times to retry a batch after a connection failure before giving up.
_MAX_RETRIES = 4
_RETRY_BACKOFF_SECONDS = 5


def _identifier(name: str) -> str:
    """Table/column names come from config, not bind variables — whitelist them."""
    if not _IDENTIFIER_RE.match(name):
        raise ValueError(f"Invalid Oracle identifier: {name!r}")
    return name


class TableSync:
    def __init__(self, config: Config):
        self.config = config
        self.checkpoint = Checkpoint(config.checkpoint_file)
        self.conn = None
        self._stop_requested = False

        missing_keys = [c for c in TARGET_KEY_COLUMNS if c not in TARGET_COLUMNS]
        if missing_keys:
            raise ValueError(
                f"TARGET_KEY_COLUMNS {missing_keys} not present in TARGET_COLUMNS"
            )

        key = _identifier(config.key_column)
        source = _identifier(config.source_table)
        target = _identifier(config.target_table)
        src_cols = ", ".join(_identifier(c) for c in SOURCE_COLUMNS)
        tgt_cols = [_identifier(c) for c in TARGET_COLUMNS]
        key_cols = [_identifier(c) for c in TARGET_KEY_COLUMNS]
        non_key_cols = [c for c in tgt_cols if c not in key_cols]

        self.select_first_sql = (
            f"SELECT {src_cols} FROM {source} "
            f"ORDER BY {key} FETCH FIRST :batch_size ROWS ONLY"
        )
        self.select_next_sql = (
            f"SELECT {src_cols} FROM {source} "
            f"WHERE {key} > :last_key "
            f"ORDER BY {key} FETCH FIRST :batch_size ROWS ONLY"
        )

        # Upsert: bind positions follow TARGET_COLUMNS, i.e. transform() output.
        using = ", ".join(f":{i + 1} AS {c}" for i, c in enumerate(tgt_cols))
        on = " AND ".join(f"t.{c} = s.{c}" for c in key_cols)
        update_set = ", ".join(f"t.{c} = s.{c}" for c in non_key_cols)
        insert_cols = ", ".join(tgt_cols)
        insert_vals = ", ".join(f"s.{c}" for c in tgt_cols)
        self.merge_sql = (
            f"MERGE INTO {target} t "
            f"USING (SELECT {using} FROM dual) s "
            f"ON ({on}) "
            f"WHEN MATCHED THEN UPDATE SET {update_set} "
            f"WHEN NOT MATCHED THEN INSERT ({insert_cols}) VALUES ({insert_vals})"
        )

        # Delete: bind positions follow TARGET_KEY_COLUMNS, i.e. transform_key().
        where = " AND ".join(f"{c} = :{i + 1}" for i, c in enumerate(key_cols))
        self.delete_sql = f"DELETE FROM {target} WHERE {where}"

        self.key_index = SOURCE_COLUMNS.index(config.key_column)
        self.op_index = SOURCE_COLUMNS.index(config.op_column)

    # -- connection handling -------------------------------------------------

    def connect(self) -> None:
        self.conn = oracledb.connect(
            user=self.config.user,
            password=self.config.password,
            dsn=self.config.dsn,
        )
        log.info("Connected to %s as %s", self.config.dsn, self.config.user)

    def close(self) -> None:
        if self.conn is not None:
            try:
                self.conn.close()
            except oracledb.Error:
                pass
            self.conn = None

    def _reconnect(self) -> None:
        self.close()
        self.connect()

    # -- batch operations ----------------------------------------------------

    def fetch_batch(self, last_key) -> list:
        with self.conn.cursor() as cursor:
            cursor.arraysize = self.config.batch_size
            cursor.prefetchrows = self.config.batch_size + 1
            if last_key is None:
                cursor.execute(self.select_first_sql, batch_size=self.config.batch_size)
            else:
                cursor.execute(
                    self.select_next_sql,
                    last_key=last_key,
                    batch_size=self.config.batch_size,
                )
            return cursor.fetchall()

    def plan_batch(self, rows: list) -> tuple:
        """Turn raw change-log rows into deduplicated upsert/delete work lists.

        Rows arrive ordered by the change key, so for several changes to the
        same target row only the last one matters: dict insertion order plus
        overwrite-on-repeat gives last-wins semantics.
        """
        ops = {
            self.config.op_insert: "upsert",
            self.config.op_update: "upsert",
            self.config.op_delete: "delete",
        }
        plan = {}  # business key -> ("upsert", target_tuple) | ("delete", key_tuple)
        for raw in rows:
            row = dict(zip(SOURCE_COLUMNS, raw))
            action = ops.get(raw[self.op_index])
            if action is None:
                log.warning(
                    "Skipping %s=%s: unknown %s value %r",
                    self.config.key_column, raw[self.key_index],
                    self.config.op_column, raw[self.op_index],
                )
                continue
            try:
                key = transform_key(row)
                payload = transform(row) if action == "upsert" else key
            except ValueError as exc:
                log.warning("Skipping row: %s", exc)
                continue
            plan[key] = (action, payload)

        upserts = [p for a, p in plan.values() if a == "upsert"]
        deletes = [p for a, p in plan.values() if a == "delete"]
        return upserts, deletes

    def apply_batch(self, upserts: list, deletes: list) -> None:
        """Apply one batch in a single transaction.

        After dedup each business key appears in exactly one list, so the
        order between the MERGE pass and the DELETE pass does not matter.
        Both statements are idempotent, making crash-replays harmless.
        """
        with self.conn.cursor() as cursor:
            if upserts:
                cursor.executemany(self.merge_sql, upserts, batcherrors=True)
                errors = cursor.getbatcherrors()
                if errors:
                    self.conn.rollback()
                    for err in errors[:10]:
                        log.error("Upsert failed at batch offset %s: %s",
                                  err.offset, err.message)
                    raise RuntimeError(
                        f"{len(errors)} row(s) failed to upsert; batch rolled back"
                    )
            if deletes:
                cursor.executemany(self.delete_sql, deletes)
        self.conn.commit()

    # -- main loop -----------------------------------------------------------

    def _request_stop(self, signum, frame):
        log.info("Received signal %s — will stop after the current batch.", signum)
        self._stop_requested = True

    def run(self) -> None:
        signal.signal(signal.SIGINT, self._request_stop)
        signal.signal(signal.SIGTERM, self._request_stop)

        last_key = self.checkpoint.load()
        rows_processed = 0
        batch_number = 0
        started = time.monotonic()

        self.connect()
        try:
            while not self._stop_requested:
                batch_number += 1
                rows = self._with_retries(self.fetch_batch, last_key)
                if not rows:
                    elapsed = time.monotonic() - started
                    log.info(
                        "Done. %d change rows applied in %d batches (%.1fs).",
                        rows_processed, batch_number - 1, elapsed,
                    )
                    self.checkpoint.clear()
                    return

                upserts, deletes = self.plan_batch(rows)
                self._with_retries(self.apply_batch, upserts, deletes)
                rows_processed += len(rows)
                last_key = rows[-1][self.key_index]
                self.checkpoint.save(last_key, rows_processed)

                log.info(
                    "Batch %d: read %d changes -> %d upserts, %d deletes "
                    "(total %d, last %s=%s)",
                    batch_number, len(rows), len(upserts), len(deletes),
                    rows_processed, self.config.key_column, last_key,
                )

                # A short batch means we are at the tail of the table; loop
                # straight back to confirm completion instead of sleeping.
                if len(rows) == self.config.batch_size:
                    log.debug("Sleeping %.1fs", self.config.sleep_seconds)
                    self._sleep(self.config.sleep_seconds)

            log.info(
                "Stopped on request after %d rows. Checkpoint saved at %s=%s; "
                "rerun to resume.", rows_processed, self.config.key_column, last_key,
            )
        finally:
            self.close()

    def _sleep(self, seconds: float) -> None:
        """Sleep in small slices so a stop signal is honored promptly."""
        deadline = time.monotonic() + seconds
        while not self._stop_requested and time.monotonic() < deadline:
            time.sleep(min(0.5, max(0.0, deadline - time.monotonic())))

    def _with_retries(self, fn, *args):
        delay = _RETRY_BACKOFF_SECONDS
        for attempt in range(1, _MAX_RETRIES + 1):
            try:
                return fn(*args)
            except oracledb.DatabaseError as exc:
                (error,) = exc.args
                recoverable = getattr(error, "isrecoverable", False)
                if not recoverable or attempt == _MAX_RETRIES:
                    raise
                log.warning(
                    "Recoverable DB error (%s) on attempt %d/%d; reconnecting in %ds",
                    error.full_code, attempt, _MAX_RETRIES, delay,
                )
                time.sleep(delay)
                delay *= 2
                self._reconnect()
