/* ============================================================================
   QuotesLab — seed
   ----------------------------------------------------------------------------
   A NOTE ON THE DATA, because it matters more than it looks like it should:

   The author names are real historical figures and the biographical columns
   are ordinary public facts. The quote *texts* are not real quotations. They
   are synthetic sentences written for this exercise. Putting invented words in
   the mouth of a named real person is a misattribution whatever the intent, and
   this database exists to exercise SQL, not to be a quotation reference — so
   nothing here is presented as something anyone actually said.

   The user rows are synthetic placeholders on example.com, which is reserved
   for exactly this by RFC 2606. No real address appears in this repository.

   Everything is deterministic: fixed identity order, fixed CreatedAt literals,
   no GETDATE(). Re-running 01 then 02 reproduces byte-identical result sets.

   Shapes deliberately planted for the Day-7 queries to find:
     * Hypatia, Seneca and Sun Tzu have no live quotes — so INNER JOIN and
       LEFT JOIN return different row counts, which is the whole lesson.
     * Hypatia has a soft-deleted quote and nothing else, so she disappears
       under a WHERE IsDeleted = 0 placed on the wrong side of a LEFT JOIN.
     * Mark Twain's most recent quote by date is soft-deleted, so dropping the
       IsDeleted filter silently changes his answer rather than erroring.
     * Ada Lovelace has two quotes at the identical CreatedAt, so a
       ROW_NUMBER ordered on CreatedAt alone is non-deterministic.
     * Several quotes have a NULL CategoryId, so an INNER JOIN to Category
       quietly loses rows.
     * The tag "testing" is used by four authors only, so the author x tag
       CROSS JOIN has real gaps to report.
   ============================================================================ */

SET NOCOUNT ON;
GO

USE QuotesLab;
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* ---------------------------------------------------------------------------
   Authors — AuthorId 1..19 in this order.
   BirthYear is negative for BCE and NULL where the year is genuinely unknown.
   --------------------------------------------------------------------------- */
INSERT app.Author (FullName, Nationality, BirthYear) VALUES
    (N'Aristotle',                N'Greek',    -384),  -- 1
    (N'Confucius',                N'Chinese',  -551),  -- 2
    (N'Marcus Aurelius',          N'Roman',     121),  -- 3
    (N'Jane Austen',              N'British',  1775),  -- 4
    (N'Mark Twain',               N'American', 1835),  -- 5
    (N'Ada Lovelace',             N'British',  1815),  -- 6
    (N'Marie Curie',              N'Polish',   1867),  -- 7
    (N'Alan Turing',              N'British',  1912),  -- 8
    (N'Grace Hopper',             N'American', 1906),  -- 9
    (N'Edsger W. Dijkstra',       N'Dutch',    1930),  -- 10
    (N'Donald Knuth',             N'American', 1938),  -- 11
    (N'Barbara Liskov',           N'American', 1939),  -- 12
    (N'Leslie Lamport',           N'American', 1941),  -- 13
    (N'C. A. R. Hoare',           N'British',  1934),  -- 14
    (N'Frederick P. Brooks Jr.',  N'American', 1931),  -- 15
    (N'Emmy Noether',             N'German',   1882),  -- 16
    (N'Hypatia',                  N'Greek',    NULL),  -- 17  no live quotes
    (N'Seneca',                   N'Roman',    NULL),  -- 18  no quotes at all
    (N'Sun Tzu',                  N'Chinese',  NULL);  -- 19  no quotes at all
GO

/* ---------------------------------------------------------------------------
   Categories — inserted root-first so CategoryId ascends with depth.
   Four levels: Science > Computer Science > Distributed Systems > Concurrency.
   --------------------------------------------------------------------------- */
INSERT app.Category (Name, ParentCategoryId) VALUES
    (N'Philosophy',            NULL),  -- 1   depth 0
    (N'Science',               NULL),  -- 2   depth 0
    (N'Literature',            NULL);  -- 3   depth 0
INSERT app.Category (Name, ParentCategoryId) VALUES
    (N'Stoicism',                 1),  -- 4   depth 1
    (N'Ethics',                   1),  -- 5   depth 1
    (N'Logic',                    1),  -- 6   depth 1
    (N'Physics',                  2),  -- 7   depth 1
    (N'Mathematics',              2),  -- 8   depth 1
    (N'Computer Science',         2),  -- 9   depth 1
    (N'Fiction',                  3),  -- 10  depth 1
    (N'Poetry',                   3);  -- 11  depth 1
INSERT app.Category (Name, ParentCategoryId) VALUES
    (N'Practical Stoicism',       4),  -- 12  depth 2
    (N'Algorithms',               9),  -- 13  depth 2
    (N'Distributed Systems',      9),  -- 14  depth 2
    (N'Programming Languages',    9),  -- 15  depth 2
    (N'Software Engineering',     9);  -- 16  depth 2
INSERT app.Category (Name, ParentCategoryId) VALUES
    (N'Concurrency',             14);  -- 17  depth 3
GO

/* Poetry (11) is a leaf that never receives a quote — it exists so the
   recursive roll-up has a genuine zero to report rather than an absent row. */

INSERT app.Tag (Name) VALUES
    (N'motivation'),   -- 1
    (N'engineering'),  -- 2
    (N'simplicity'),   -- 3
    (N'failure'),      -- 4
    (N'learning'),     -- 5
    (N'leadership'),   -- 6
    (N'design'),       -- 7
    (N'testing');      -- 8
GO

/* ---------------------------------------------------------------------------
   Quotes — QuoteId 1..83, grouped by author in AuthorId order.
   Texts are synthetic; see the header.
   --------------------------------------------------------------------------- */
INSERT app.Quote (AuthorId, CategoryId, QuoteText, CreatedAt, IsDeleted) VALUES
    -- Aristotle (1..4)
    ( 1,    5, N'Excellence is a habit before it is an outcome.',                      '2026-05-04T08:10:00', 0),
    ( 1,    6, N'A conclusion is only as sound as the premise you skipped.',           '2026-05-19T14:22:00', 0),
    ( 1,    5, N'The mean is not the average; it is the fitting.',                     '2026-06-11T09:05:00', 0),
    ( 1, NULL, N'Naming a thing well is half of understanding it.',                    '2026-07-22T16:40:00', 0),
    -- Confucius (5..7)
    ( 2,    5, N'Correct the small fault today or inherit it tomorrow.',               '2026-05-07T11:30:00', 0),
    ( 2,    1, N'Study without reflection is copying.',                                '2026-06-25T07:45:00', 0),
    ( 2,    5, N'Ask the question you are afraid answers you.',                        '2026-08-02T18:15:00', 0),
    -- Marcus Aurelius (8..11)
    ( 3,    4, N'What blocks the road is the road.',                                   '2026-05-12T06:00:00', 0),
    ( 3,   12, N'Begin before you feel ready; readiness is a result.',                 '2026-06-02T06:30:00', 0),
    ( 3,    4, N'You do not control the tide, only the boat.',                         '2026-07-09T20:10:00', 0),
    ( 3,   12, N'Complaint is the most expensive way to pass time.',                   '2026-08-05T05:55:00', 0),
    -- Jane Austen (12..14)
    ( 4,   10, N'Character shows in what a person finds unremarkable.',                '2026-05-15T13:05:00', 0),
    ( 4,   10, N'Manners are the interface; motives are the implementation.',          '2026-06-18T15:35:00', 0),
    ( 4,    3, N'A good sentence earns its second clause.',                            '2026-07-30T11:20:00', 0),
    -- Mark Twain (15..20 live, 21 soft-deleted and later than every live one)
    ( 5,   10, N'The rehearsal is where the improvisation is written.',                '2026-05-02T09:00:00', 0),
    ( 5,    3, N'Brevity takes the longest to draft.',                                 '2026-05-21T17:45:00', 0),
    ( 5,   10, N'Everyone edits; few admit to the second pass.',                       '2026-06-08T12:15:00', 0),
    ( 5, NULL, N'Plans survive contact with breakfast, rarely lunch.',                 '2026-06-29T08:40:00', 0),
    ( 5,    3, N'The joke that explains itself has already left.',                     '2026-07-17T19:25:00', 0),
    ( 5,   10, N'Travel cures certainty faster than argument.',                        '2026-08-09T10:50:00', 0),
    ( 5,    3, N'Draft withdrawn during the archive cleanup.',                         '2026-08-15T09:00:00', 1),
    -- Ada Lovelace (22..23) — identical CreatedAt, deliberately
    ( 6,    8, N'A machine follows the argument, never the intention.',                '2026-07-04T10:00:00', 0),
    ( 6,    9, N'Notation is the first optimisation.',                                 '2026-07-04T10:00:00', 0),
    -- Marie Curie (24..25)
    ( 7,    7, N'Measure twice, because the instrument lies once.',                    '2026-05-26T08:20:00', 0),
    ( 7,    2, N'Curiosity outlasts funding, which is why it is dangerous.',           '2026-07-13T14:10:00', 0),
    -- Alan Turing (26..30)
    ( 8,    6, N'Every decidable question is one that somebody bounded first.',        '2026-05-09T10:35:00', 0),
    ( 8,    9, N'The machine is indifferent to your metaphor for it.',                 '2026-06-05T16:05:00', 0),
    ( 8,   13, N'A shorter proof is usually a better algorithm in disguise.',          '2026-06-27T09:55:00', 0),
    ( 8,    9, N'Imitation is a test, not a compliment.',                              '2026-07-19T13:40:00', 0),
    ( 8, NULL, N'A question worth asking survives a precise phrasing.',                '2026-08-11T07:30:00', 0),
    -- Grace Hopper (31..34)
    ( 9,   15, N'A language people can read is a language people will fix.',           '2026-05-17T11:15:00', 0),
    ( 9,   16, N'Permission is the slowest dependency in any build.',                  '2026-06-14T14:45:00', 0),
    ( 9,   16, N'Precedent is not a requirement, however loudly it is cited.',         '2026-07-06T09:30:00', 0),
    ( 9,    9, N'A nanosecond is a length before it is a duration.',                   '2026-08-13T15:20:00', 0),
    -- Edsger W. Dijkstra (35..46 live, 47..48 soft-deleted)
    (10,   16, N'Simplicity is bought, and the price is thinking.',                    '2026-05-01T08:00:00', 0),
    (10,   13, N'An algorithm you cannot argue about, you cannot trust.',              '2026-05-06T09:20:00', 0),
    (10,   15, N'A construct that surprises the reader has cost more than it saved.',  '2026-05-13T10:40:00', 0),
    (10,    6, N'Proof is the only review that never gets tired.',                     '2026-05-20T12:00:00', 0),
    (10,   17, N'Two threads and one assumption make three outcomes.',                 '2026-05-27T13:15:00', 0),
    (10,    9, N'Abstraction is precision at a distance, not vagueness with a title.', '2026-06-03T08:45:00', 0),
    (10,   13, N'Complexity that is merely hidden is complexity that is compounding.', '2026-06-10T15:30:00', 0),
    (10,   16, N'A schedule is an opinion until the hard part is finished.',           '2026-06-17T11:05:00', 0),
    (10,   15, N'Every convenience in a language is a tax on the reader.',             '2026-06-24T16:50:00', 0),
    (10,   17, N'Ordering is a design decision, not a runtime accident.',              '2026-07-01T07:35:00', 0),
    (10, NULL, N'The bug you cannot reproduce is the design you cannot explain.',      '2026-07-15T14:25:00', 0),
    (10,   16, N'Elegance is what remains once the special cases are understood.',     '2026-08-16T09:10:00', 0),
    (10,    9, N'Superseded note retained for revision history.',                      '2026-06-20T10:00:00', 1),
    (10,   13, N'Duplicate entry removed during deduplication.',                       '2026-07-25T10:00:00', 1),
    -- Donald Knuth (49..57)
    (11,   13, N'Measure before you mourn the constant factor.',                       '2026-05-05T08:30:00', 0),
    (11,    8, N'A closed form is a compliment the problem pays you.',                 '2026-05-18T10:10:00', 0),
    (11,   16, N'Premature certainty costs more than premature optimisation.',         '2026-06-01T09:40:00', 0),
    (11,   15, N'Programs are written for people and merely tolerated by machines.',   '2026-06-13T14:20:00', 0),
    (11,    3, N'Typesetting is an argument about attention.',                         '2026-06-26T16:00:00', 0),
    (11,   13, N'The average case is a story about your inputs, not your code.',       '2026-07-08T08:55:00', 0),
    (11,    8, N'A generating function turns counting into algebra.',                  '2026-07-21T12:30:00', 0),
    (11,   16, N'Proved is not the same as run, and both are cheaper than shipped.',   '2026-08-01T11:45:00', 0),
    (11,   13, N'Analysis without a machine is a hypothesis.',                         '2026-08-14T13:00:00', 0),
    -- Barbara Liskov (58..62)
    (12,   15, N'A subtype must be usable wherever its base is expected.',             '2026-05-11T09:15:00', 0),
    (12,   16, N'A module boundary you cannot state in a sentence is not a boundary.', '2026-06-06T13:50:00', 0),
    (12,    9, N'Abstraction earns its keep the second time it is used.',              '2026-07-02T10:25:00', 0),
    (12,   14, N'A replicated system is an agreement problem in a storage costume.',   '2026-07-27T15:10:00', 0),
    (12,   16, N'The interface is the contract; the code is only evidence.',           '2026-08-08T08:05:00', 0),
    -- Leslie Lamport (63..67)
    (13,   14, N'In a distributed system, the machine you forgot is the one that pages you.', '2026-05-23T11:40:00', 0),
    (13,   17, N'Time is a partial order that everyone insists on totalling.',         '2026-06-09T09:25:00', 0),
    (13,    6, N'If you cannot write the invariant, you are guessing on purpose.',     '2026-07-05T14:55:00', 0),
    (13,    8, N'A specification is a program you are allowed to be honest in.',       '2026-07-28T10:15:00', 0),
    (13,   14, N'Consensus is expensive because disagreement is cheap.',               '2026-08-10T16:35:00', 0),
    -- C. A. R. Hoare (68..74)
    (14,   13, N'Partition first; the ordering falls out of the argument.',            '2026-05-08T08:25:00', 0),
    (14,   15, N'A null reference is an absent value that forgot to say so.',          '2026-05-30T12:05:00', 0),
    (14,    6, N'Obvious correctness and no obvious defects are different results.',   '2026-06-16T10:45:00', 0),
    (14,   17, N'Processes that communicate are easier to reason about than processes that share.', '2026-07-03T15:20:00', 0),
    (14,   16, N'A design review is cheaper than a post-mortem, and less attended.',   '2026-07-20T09:35:00', 0),
    (14,   15, N'A type system is a conversation held before the argument.',           '2026-08-04T13:15:00', 0),
    (14,   13, N'The average of a good algorithm still has a bad day.',                '2026-08-15T07:50:00', 0),
    -- Frederick P. Brooks Jr. (75..81)
    (15,   16, N'A late project absorbs new people the way sand absorbs water.',       '2026-05-03T10:20:00', 0),
    (15,   16, N'The second system is where restraint goes to die.',                   '2026-05-25T14:35:00', 0),
    (15,   16, N'The first build is a question; only the second is an answer.',        '2026-06-12T08:15:00', 0),
    (15,    9, N'Conceptual integrity survives committees only by accident.',          '2026-06-30T16:25:00', 0),
    (15, NULL, N'Estimates are confidence intervals that lost their error bars.',      '2026-07-14T11:55:00', 0),
    (15,   16, N'No single technique doubles productivity; several together might.',   '2026-08-03T09:45:00', 0),
    (15,   16, N'Documentation is the part of a design that outlives the designer.',   '2026-08-12T14:05:00', 0),
    -- Emmy Noether (82)
    (16,    8, N'Every symmetry is a conservation law waiting to be named.',           '2026-06-21T10:30:00', 0),
    -- Hypatia (83) — her only row, and it is soft-deleted
    (17, NULL, N'Fragment withdrawn pending attribution review.',                      '2026-06-22T09:00:00', 1);
GO

/* ---------------------------------------------------------------------------
   Quote tags. Quotes 18, 21, 30, 47, 48, 53, 55 and 83 are deliberately left
   untagged, and the "testing" tag reaches only four authors.
   --------------------------------------------------------------------------- */
INSERT app.QuoteTag (QuoteId, TagId) VALUES
    ( 1,5),( 2,5),( 3,7),( 4,5),
    ( 5,5),( 6,5),( 7,1),
    ( 8,1),( 9,1),(10,1),(11,1),
    (12,7),(13,7),(14,3),
    (15,5),(16,3),(17,3),(19,3),(20,5),
    (22,2),(23,3),
    (24,5),(25,1),
    (26,5),(27,2),(28,2),(29,5),(29,8),
    (31,2),(32,6),(33,6),(34,2),
    (35,2),(35,3),(36,2),(37,3),(38,2),(39,2),(39,8),(40,7),(41,3),
    (42,6),(43,3),(44,2),(45,4),(46,3),
    (49,2),(50,2),(51,3),(52,7),(52,8),(54,2),(56,4),(57,2),
    (58,7),(59,7),(60,7),(61,2),(62,7),
    (63,4),(64,2),(65,2),(66,7),(67,2),
    (68,2),(69,4),(70,3),(71,2),(72,6),(72,8),(73,7),(74,2),
    (75,6),(76,4),(77,4),(78,6),(79,6),(80,6),(81,7),
    (82,5);
GO

/* ---------------------------------------------------------------------------
   Users and collections. "Empty shelf" holds nothing on purpose.
   Addresses are on example.com, reserved for documentation by RFC 2606.
   --------------------------------------------------------------------------- */
INSERT app.AppUser (Email, DisplayName) VALUES
    (N'reader.one@example.com',   N'Reader One'),    -- 1
    (N'reader.two@example.com',   N'Reader Two'),    -- 2
    (N'reader.three@example.com', N'Reader Three');  -- 3
GO

INSERT app.Collection (OwnerUserId, Name) VALUES
    (1, N'Engineering essentials'),        -- 1
    (1, N'Stoic mornings'),                -- 2
    (2, N'Distributed systems reading'),   -- 3
    (3, N'Empty shelf');                   -- 4
GO

INSERT app.CollectionItem (CollectionId, QuoteId, AddedAt) VALUES
    (1, 35, '2026-08-01T09:00:00'),
    (1, 49, '2026-08-01T09:05:00'),
    (1, 58, '2026-08-01T09:10:00'),
    (1, 68, '2026-08-02T18:20:00'),
    (1, 75, '2026-08-02T18:25:00'),
    (2,  8, '2026-06-01T07:00:00'),
    (2,  9, '2026-06-01T07:01:00'),
    (2, 10, '2026-07-10T07:15:00'),
    (2, 11, '2026-08-06T07:20:00'),
    (3, 61, '2026-07-28T20:00:00'),
    (3, 63, '2026-07-28T20:02:00'),
    (3, 64, '2026-07-28T20:04:00'),
    (3, 67, '2026-08-11T21:30:00');
GO

/* ---------------------------------------------------------------------------
   Seed verification — cheap, and it catches a half-applied script immediately.
   --------------------------------------------------------------------------- */
SELECT
    (SELECT COUNT(*) FROM app.Author)                        AS Authors,
    (SELECT COUNT(*) FROM app.Category)                      AS Categories,
    (SELECT COUNT(*) FROM app.Quote)                         AS QuotesTotal,
    (SELECT COUNT(*) FROM app.Quote WHERE IsDeleted = 0)     AS QuotesLive,
    (SELECT COUNT(*) FROM app.Tag)                           AS Tags,
    (SELECT COUNT(*) FROM app.QuoteTag)                      AS QuoteTags,
    (SELECT COUNT(*) FROM app.AppUser)                       AS Users,
    (SELECT COUNT(*) FROM app.Collection)                    AS Collections,
    (SELECT COUNT(*) FROM app.CollectionItem)                AS CollectionItems;
GO

PRINT 'QuotesLab seeded.';
GO
