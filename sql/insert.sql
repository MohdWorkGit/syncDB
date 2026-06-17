-- INSERT_SQL_FILE: applied to each source row whose OPERATION = OP_INSERT ('I').
--
-- Every :NAME is bound from the source change-log row by column name. This is the
-- one place the source->target transformation lives now — edit it freely:
--   * CUSTOMER_NAME is the trimmed first + last name (UNKNOWN if both blank)
--   * ORDER_DATE is built from the three text date parts (a bad date, e.g. month
--     13, makes Oracle reject the row -> FAILED, and the key is quarantined)
--   * TOTAL_AMOUNT is unit price * quantity
--   * STATUS / REGION map the source codes to readable values
--
-- A plain INSERT (not MERGE) because Oracle only supports RETURNING ... INTO on
-- INSERT/UPDATE/DELETE. When LINK_SQL_FILE is set, the new row's id MUST be
-- returned into :NEW_ID so the link row can reference it.
INSERT INTO order_summary
    (order_id, customer_name, order_date, total_amount, status, region, synced_at)
VALUES (
    :ORDER_ID,
    NVL(NULLIF(TRIM(TRIM(:CUST_FIRST_NAME) || ' ' || TRIM(:CUST_LAST_NAME)), ''), 'UNKNOWN'),
    TO_DATE(:ORDER_DAY || '/' || :ORDER_MONTH || '/' || :ORDER_YEAR, 'DD/MM/YYYY'),
    ROUND(:UNIT_PRICE * :QUANTITY, 2),
    DECODE(:STATUS_CODE, 'NW', 'NEW', 'SH', 'SHIPPED', 'DL', 'DELIVERED', 'CN', 'CANCELLED', 'UNKNOWN'),
    DECODE(:COUNTRY_CODE,
        'US', 'AMERICAS', 'CA', 'AMERICAS', 'BR', 'AMERICAS', 'MX', 'AMERICAS',
        'GB', 'EMEA', 'DE', 'EMEA', 'FR', 'EMEA', 'AE', 'EMEA', 'SA', 'EMEA',
        'IN', 'APAC', 'JP', 'APAC', 'CN', 'APAC', 'AU', 'APAC', 'OTHER'),
    SYSTIMESTAMP
)
RETURNING summary_id INTO :NEW_ID
