"""Row transformation: maps a source change-log row to the target structure.

This module is the one you edit to adapt the project to your own tables.

The source is a *change log*: every row describes one change to apply to the
target — an insert, an update, or a delete — identified by an operation
column. The shipped example reads ORDER_CHANGES and maintains a flattened
ORDER_SUMMARY reporting table (see sql/example_tables.sql):

    - first/last name are merged into a single CUSTOMER_NAME
    - the order date is split into ORDER_YEAR / ORDER_MONTH
    - the total is computed from UNIT_PRICE * QUANTITY
    - 2-letter status codes are decoded into readable values
    - country codes are bucketed into regions

Delete rows usually carry only the business key (other columns NULL), which
is why key extraction (transform_key) is separate from full transformation
(transform).
"""

from datetime import datetime

# Columns fetched from the source change-log table. Must include the paging
# key (KEY_COLUMN, e.g. CHANGE_ID) and the operation column (OP_COLUMN).
SOURCE_COLUMNS = [
    "CHANGE_ID",
    "OPERATION",
    "ORDER_ID",
    "CUST_FIRST_NAME",
    "CUST_LAST_NAME",
    "ORDER_DATE",
    "UNIT_PRICE",
    "QUANTITY",
    "STATUS_CODE",
    "COUNTRY_CODE",
]

# Columns written to the target table, in the order produced by transform().
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

# Column(s) that uniquely identify a row in the TARGET table. Used to match
# rows for MERGE (insert/update) and DELETE, and to deduplicate changes to
# the same row within a batch. Must be a subset of TARGET_COLUMNS.
TARGET_KEY_COLUMNS = ["ORDER_ID"]

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


def transform_key(row: dict) -> tuple:
    """Extract the target business key from a source row (any operation)."""
    return (row["ORDER_ID"],)


def transform(row: dict) -> tuple:
    """Turn one insert/update source row into a target tuple.

    Returning a tuple ordered like TARGET_COLUMNS keeps executemany() fast.
    Raise ValueError to have the sync engine log and skip a bad row.
    Never called for delete rows — those only need transform_key().
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
