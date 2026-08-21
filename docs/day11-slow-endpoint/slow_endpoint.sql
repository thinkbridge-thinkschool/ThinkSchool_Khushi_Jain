-- Day 11 baseline data: 5,000 quotes across 200 authors.
-- Run against quotes-day11.db, not the ordinary dev database: this clears the table first.
DELETE FROM Quotes;

WITH RECURSIVE row_number(i) AS (
    SELECT 1
    UNION ALL
    SELECT i + 1 FROM row_number WHERE i < 5000
)
INSERT INTO Quotes (Author, Text, IsDeleted, OwnerId)
SELECT
    'Author ' || printf('%03d', (i % 200) + 1),
    'Quote number ' || i,
    0,
    NULL
FROM row_number;

-- The plan for the author list, run once per request.
EXPLAIN QUERY PLAN
SELECT DISTINCT "q"."Author"
FROM "Quotes" AS "q"
WHERE NOT ("q"."IsDeleted");

-- The plan for the per-author read, run 200 times per request.
-- The literal stands in for EF Core's @author parameter.
EXPLAIN QUERY PLAN
SELECT "q"."Id", "q"."Author", "q"."IsDeleted", "q"."OwnerId", "q"."Text"
FROM "Quotes" AS "q"
WHERE NOT ("q"."IsDeleted") AND "q"."Author" = 'Author 001';
