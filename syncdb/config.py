"""Configuration loaded from environment variables (optionally via a .env file)."""

import os
from dataclasses import dataclass

from dotenv import load_dotenv

load_dotenv()


@dataclass(frozen=True)
class Config:
    user: str
    password: str
    dsn: str

    batch_size: int
    sleep_seconds: float

    source_table: str
    target_table: str
    key_column: str

    op_column: str
    op_insert: str
    op_update: str
    op_delete: str

    checkpoint_file: str
    log_level: str

    @classmethod
    def from_env(cls) -> "Config":
        missing = [v for v in ("ORACLE_USER", "ORACLE_PASSWORD", "ORACLE_DSN") if not os.getenv(v)]
        if missing:
            raise SystemExit(
                f"Missing required environment variables: {', '.join(missing)}. "
                "Copy .env.example to .env and fill it in."
            )

        return cls(
            user=os.environ["ORACLE_USER"],
            password=os.environ["ORACLE_PASSWORD"],
            dsn=os.environ["ORACLE_DSN"],
            batch_size=int(os.getenv("BATCH_SIZE", "1000")),
            sleep_seconds=float(os.getenv("SLEEP_SECONDS", "5")),
            source_table=os.getenv("SOURCE_TABLE", "ORDER_CHANGES"),
            target_table=os.getenv("TARGET_TABLE", "ORDER_SUMMARY"),
            key_column=os.getenv("KEY_COLUMN", "CHANGE_ID"),
            op_column=os.getenv("OP_COLUMN", "OPERATION"),
            op_insert=os.getenv("OP_INSERT", "I"),
            op_update=os.getenv("OP_UPDATE", "U"),
            op_delete=os.getenv("OP_DELETE", "D"),
            checkpoint_file=os.getenv("CHECKPOINT_FILE", ".sync_checkpoint.json"),
            log_level=os.getenv("LOG_LEVEL", "INFO"),
        )
