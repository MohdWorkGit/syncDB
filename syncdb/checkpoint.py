"""Checkpoint persistence so an interrupted sync resumes where it stopped.

The checkpoint stores the last successfully committed key value. Writes are
atomic (write to a temp file, then rename) so a crash mid-write cannot leave
a corrupt checkpoint behind.
"""

import json
import logging
import os
import tempfile

log = logging.getLogger(__name__)


class Checkpoint:
    def __init__(self, path: str):
        self.path = path

    def load(self):
        """Return the last committed key, or None if starting fresh."""
        if not os.path.exists(self.path):
            return None
        with open(self.path, "r", encoding="utf-8") as fh:
            data = json.load(fh)
        last_key = data.get("last_key")
        log.info("Resuming from checkpoint: last_key=%s (%s rows done so far)",
                 last_key, data.get("rows_processed", "?"))
        return last_key

    def save(self, last_key, rows_processed: int) -> None:
        data = {"last_key": last_key, "rows_processed": rows_processed}
        directory = os.path.dirname(os.path.abspath(self.path))
        fd, tmp_path = tempfile.mkstemp(dir=directory, prefix=".ckpt-")
        try:
            with os.fdopen(fd, "w", encoding="utf-8") as fh:
                json.dump(data, fh)
            os.replace(tmp_path, self.path)
        except BaseException:
            if os.path.exists(tmp_path):
                os.remove(tmp_path)
            raise

    def clear(self) -> None:
        if os.path.exists(self.path):
            os.remove(self.path)
            log.info("Sync finished; checkpoint file removed.")
