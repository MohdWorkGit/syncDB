"""Row transformation: maps a source row to the target table's structure.

This module is the one you edit to adapt the project to your own tables.
The shipped example reads a wide legacy SOURCE_ORDERS table and produces
rows for a flattened ORDER_SUMMARY reporting table (see sql/example_tables.sql):

    - first/last name are merged into a single CUSTOMER_NAME
    - the order date is split into ORDER_YEAR / ORDER_MONTH
    - the total is computed from UNIT_PRICE * QUANTITY
    - 2-letter status codes are decoded into readable values
    - country codes are bucketed into regions
"""

from datetime import datetime

# Columns fetched from the source table. The key column must be first so the
# sync engine can track checkpoints without guessing.
SOURCE_COLUMNS = [
    "ORDER_ID",
    "CUST_FIRST_NAME",
    "CUST_LAST_NAME",
    "ORDER_DATE",
    "UNIT_PRICE",
    "QUANTITY",
    "STATUS_CODE",
    "COUNTRY_CODE",
]

# Columns inserted into the target table, in the order produced by transform().
TARGET_COLUMNS = [
    "ORDER_ID",
    "CUSTOMER_NAME",
    "ORDER_YEAR",
    "ORDER_MONTH",
    "TOTAL_AMOUNT",
    "STATUS",
    "REGION",
    "SYNCED_AT",
]

_STATUS_MAP = {
    "NW": "NEW",
    "SH": "SHIPPED",
    "DL": "DELIVERED",
    "CN": "CANCELLED",
}

_REGION_MAP = {
    "US": "AMERICAS", "CA": "AMERICAS", "BR": "AMERICAS", "MX": "AMERICAS",
    "GB": "EMEA", "DE": "EMEA", "FR": "EMEA", "AE": "EMEA", "SA": "EMEA",
    "IN": "APAC", "JP": "APAC", "CN": "APAC", "AU": "APAC",
}


def transform(row: dict) -> tuple:
    """Turn one source row (dict keyed by SOURCE_COLUMNS) into a target tuple.

    Returning a tuple ordered like TARGET_COLUMNS keeps executemany() fast.
    Raise ValueError to have the sync engine log and skip a bad row.
    """
    first = (row["CUST_FIRST_NAME"] or "").strip()
    last = (row["CUST_LAST_NAME"] or "").strip()
    customer_name = " ".join(p for p in (first, last) if p) or "UNKNOWN"

    order_date = row["ORDER_DATE"]
    if order_date is None:
        raise ValueError(f"ORDER_ID={row['ORDER_ID']}: ORDER_DATE is NULL")

    unit_price = row["UNIT_PRICE"] or 0
    quantity = row["QUANTITY"] or 0

    return (
        row["ORDER_ID"],
        customer_name,
        order_date.year,
        order_date.month,
        round(unit_price * quantity, 2),
        _STATUS_MAP.get(row["STATUS_CODE"], "UNKNOWN"),
        _REGION_MAP.get(row["COUNTRY_CODE"], "OTHER"),
        datetime.now(),
    )
