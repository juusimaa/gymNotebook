-- The fingerprint of Gym Notebook's data, used to prove two databases hold the
-- same thing: Neon against UpCloud at cutover (FR-008, SC-002), a restore
-- against the counts it should have (deploy/README.md § Restore), the final
-- Neon archive against the cutover output. Modelled on the tracker's
-- deploy/bin/verify-copy.sql.
--
-- Run it on each side and diff the two outputs; they must be byte-identical:
--
--   psql -XAt -v ON_ERROR_STOP=1 -f verify-copy.sql "$URL" > side.txt
--
-- -X skips any ~/.psqlrc, -A -t print bare `name|count` lines with no header
-- or padding. Read-only (plain SELECTs, no temp objects), so it also runs on
-- Neon after the freeze has made it read-only. No row content is printed,
-- only counts and the migration id, so the output is safe to paste into a PR.

-- One `table|rows` line per application table in `public`, found from the
-- catalog so a table added by a later migration is counted without editing
-- this file. Exact count(*), not pg_class.reltuples, which is an estimate.
-- query_to_xml runs the per-table count inside a single SELECT. COLLATE "C"
-- makes the order the same whatever collation each server defaults to.
SELECT t.table_name,
       (xpath('/row/c/text()',
              query_to_xml(format('SELECT count(*) AS c FROM public.%I', t.table_name),
                           false, true, '')))[1]::text::bigint AS row_count
FROM information_schema.tables AS t
WHERE t.table_schema = 'public'
  AND t.table_type = 'BASE TABLE'
  AND t.table_name <> '__EFMigrationsHistory'
ORDER BY t.table_name COLLATE "C";

-- The newest applied migration: a copy taken at a different schema version
-- could match on every count and still be wrong. EF migration ids start with
-- a sortable timestamp.
SELECT 'migration|' || migration_id
FROM "__EFMigrationsHistory"
ORDER BY migration_id COLLATE "C" DESC
LIMIT 1;
