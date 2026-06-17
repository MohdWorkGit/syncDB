-- UPDATE_SQL_FILE: applied to each source row whose OPERATION = OP_UPDATE ('U').
--
-- Same column transforms as insert.sql, but UPDATEs the existing target row by
-- its business key. A 'U' for an order that does not exist updates zero rows
-- (no error) — adjust to taste if you want an upsert instead.
UPDATE order_summary SET
    customer_name = NVL(NULLIF(TRIM(TRIM(:CUST_FIRST_NAME) || ' ' || TRIM(:CUST_LAST_NAME)), ''), 'UNKNOWN'),
    order_date    = TO_DATE(:ORDER_DAY || '/' || :ORDER_MONTH || '/' || :ORDER_YEAR, 'DD/MM/YYYY'),
    total_amount  = ROUND(:UNIT_PRICE * :QUANTITY, 2),
    status        = DECODE(:STATUS_CODE, 'NW', 'NEW', 'SH', 'SHIPPED', 'DL', 'DELIVERED', 'CN', 'CANCELLED', 'UNKNOWN'),
    region        = DECODE(:COUNTRY_CODE,
        'US', 'AMERICAS', 'CA', 'AMERICAS', 'BR', 'AMERICAS', 'MX', 'AMERICAS',
        'GB', 'EMEA', 'DE', 'EMEA', 'FR', 'EMEA', 'AE', 'EMEA', 'SA', 'EMEA',
        'IN', 'APAC', 'JP', 'APAC', 'CN', 'APAC', 'AU', 'APAC', 'OTHER'),
    synced_at     = SYSTIMESTAMP
WHERE order_id = :ORDER_ID
