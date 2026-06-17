-- LINK_SQL_FILE (optional): runs once after each successful insert, linking the
-- new target row to a user. Nothing here comes from the source row:
--   * :NEW_ID is the id of the row insert.sql just created (its RETURNING value)
--   * :SYNC_USER_ID and :SYNC_USER_SECTION are resolved from the environment
--     (set them in .env) — any other :NAME here is also read from an env var.
--
-- link_id is the table's own identity PK; we only supply the new summary_id and
-- the user values. A failed link is recorded as LINK_FAILED and the insert kept.
INSERT INTO order_user_link (summary_id, user_id, user_section)
VALUES (:NEW_ID, :SYNC_USER_ID, :SYNC_USER_SECTION)
