#!/usr/bin/env python3
"""Entry point: throttled copy of a large Oracle table into a reshaped target.

Usage:
    cp .env.example .env   # then edit credentials / table names / pacing
    pip install -r requirements.txt
    python run_sync.py [--batch-size N] [--sleep SECONDS] [--restart]
"""

import argparse
import dataclasses
import logging
import sys

from syncdb.config import Config
from syncdb.sync import TableSync


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--batch-size", type=int, help="rows per cycle (overrides BATCH_SIZE)")
    parser.add_argument("--sleep", type=float, help="seconds between cycles (overrides SLEEP_SECONDS)")
    parser.add_argument("--restart", action="store_true",
                        help="ignore any existing checkpoint and start from the beginning")
    args = parser.parse_args()

    config = Config.from_env()
    overrides = {}
    if args.batch_size is not None:
        overrides["batch_size"] = args.batch_size
    if args.sleep is not None:
        overrides["sleep_seconds"] = args.sleep
    if overrides:
        config = dataclasses.replace(config, **overrides)

    logging.basicConfig(
        level=config.log_level.upper(),
        format="%(asctime)s %(levelname)-8s %(name)s: %(message)s",
    )

    sync = TableSync(config)
    if args.restart:
        sync.checkpoint.clear()

    try:
        sync.run()
    except Exception:
        logging.getLogger(__name__).exception("Sync aborted")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
