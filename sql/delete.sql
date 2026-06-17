-- DELETE_SQL_FILE: applied to each source row whose OPERATION = OP_DELETE ('D').
--
-- Delete rows usually carry only the business key. The order_user_link table has
-- ON DELETE CASCADE on its summary_id foreign key (see example_tables.sql), so
-- removing the target row also removes its link rows — link cleanup lives in your
-- schema, not in the engine. Deleting a non-existent order removes zero rows.
DELETE FROM order_summary WHERE order_id = :ORDER_ID
