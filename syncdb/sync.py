"""Core sync engine: throttled, resumable, batch copy between two Oracle tables.

Strategy
--------
The source table is read with *keyset pagination* on an indexed key column:

    SELECT ... WHERE key > :last_key ORDER BY key FETCH FIRST :n ROWS ONLY

Each query touches only the next slice of the index, so the cost per batch is
flat no matter how large the table is — unlike OFFSET pagination, which gets
slower the deeper it goes, and unlike one giant open cursor, which risks
ORA-01555 (snapshot too old) on long runs.

After every batch the engine commits, persists a checkpoint, and sleeps, so
the database only ever sees short bursts of light work. If the process dies
it resumes from the checkpoint; duplicate inserts from the small
crash-between-commit-and-checkpoint window are detected via ORA-00001 and
skipped.
"""

import logging
import re
import signal
import time

import oracledb

from .checkpoint import Checkpoint
from .config import Config
from .transformer import SOURCE_COLUMNS, TARGET_COLUMNS, transform

log = logging.getLogger(__name__)

_IDENTIFIER_RE = re.compile(r"^[A-Za-z][A-Za-z0-9_$#]*$")

# How many times to retry a batch after a connection failure before giving up.
_MAX_RETRIES = 4
_RETRY_BACKOFF_SECONDS = 5

_ORA_UNIQUE_VIOLATION = 1  # ORA-00001


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

        key = _identifier(config.key_column)
        src_cols = ", ".join(_identifier(c) for c in SOURCE_COLUMNS)
        tgt_cols = ", ".join(_identifier(c) for c in TARGET_COLUMNS)
        binds = ", ".join(f":{i + 1}" for i in range(len(TARGET_COLUMNS)))

        self.select_first_sql = (
            f"SELECT {src_cols} FROM {_identifier(config.source_table)} "
            f"ORDER BY {key} FETCH FIRST :batch_size ROWS ONLY"
        )
        self.select_next_sql = (
            f"SELECT {src_cols} FROM {_identifier(config.source_table)} "
            f"WHERE {key} > :last_key "
            f"ORDER BY {key} FETCH FIRST :batch_size ROWS ONLY"
        )
        self.insert_sql = (
            f"INSERT INTO {_identifier(config.target_table)} ({tgt_cols}) VALUES ({binds})"
        )
        self.key_index = SOURCE_COLUMNS.index(config.key_column)

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

    def transform_batch(self, rows: list) -> list:
        out = []
        for raw in rows:
            row = dict(zip(SOURCE_COLUMNS, raw))
            try:
                out.append(transform(row))
            except ValueError as exc:
                log.warning("Skipping row: %s", exc)
        return out

    def insert_batch(self, data: list) -> int:
        """Insert transformed rows; returns the number actually inserted.

        batcherrors=True lets the rest of the batch proceed when individual
        rows fail. Unique-constraint violations (rows already copied before a
        crash) are skipped quietly; anything else aborts the run.
        """
        if not data:
            return 0
        with self.conn.cursor() as cursor:
            cursor.executemany(self.insert_sql, data, batcherrors=True)
            errors = cursor.getbatcherrors()

        duplicates = [e for e in errors if e.full_code == f"ORA-{_ORA_UNIQUE_VIOLATION:05d}"]
        fatal = [e for e in errors if e.full_code != f"ORA-{_ORA_UNIQUE_VIOLATION:05d}"]

        if fatal:
            self.conn.rollback()
            for err in fatal[:10]:
                log.error("Insert failed at batch offset %s: %s", err.offset, err.message)
            raise RuntimeError(f"{len(fatal)} row(s) failed to insert; batch rolled back")

        self.conn.commit()
        if duplicates:
            log.info("Skipped %d row(s) already present in target", len(duplicates))
        return len(data) - len(duplicates)

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
                        "Done. %d rows copied in %d batches (%.1fs).",
                        rows_processed, batch_number - 1, elapsed,
                    )
                    self.checkpoint.clear()
                    return

                inserted = self._with_retries(
                    self.insert_batch, self.transform_batch(rows)
                )
                rows_processed += len(rows)
                last_key = rows[-1][self.key_index]
                self.checkpoint.save(last_key, rows_processed)

                log.info(
                    "Batch %d: read %d, inserted %d (total %d, last %s=%s)",
                    batch_number, len(rows), inserted, rows_processed,
                    self.config.key_column, last_key,
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
