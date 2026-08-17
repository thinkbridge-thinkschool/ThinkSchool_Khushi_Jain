/* ============================================================================
   QuotesLab — schema
   ----------------------------------------------------------------------------
   A SQL Server / Azure SQL rendering of the Week-1 Quotes domain, widened so
   that joins and CTEs have something real to bite on. Three things the shipped
   EF Core model does not have, and this schema does:

     * Author is a table, not an nvarchar column on Quote. Week 1 stores the
       author's name inline, so "quotes per author" is a string GROUP BY and
       an author with no quotes cannot be represented at all. Normalising it
       is what turns the exercise's LEFT JOIN into a meaningful one.
     * Quote.CreatedAt exists. Week 1 has no timestamp of any kind, so
       "their most-recent quote" has no answer. Ordering by identity would
       work by accident, not by design.
     * Category is a self-referencing tree. Nothing in the shipped model is
       hierarchical, and a recursive CTE needs somewhere to recurse.

   Running this drops and recreates the database, so it is safe to re-run and
   the identity values -- and therefore every result set below -- are stable.

   Target: SQL Server 2022. Everything here is Azure SQL compatible except the
   CREATE DATABASE batch, which Azure SQL takes without the file-management
   options we are not using anyway.
   ============================================================================ */

SET NOCOUNT ON;
GO

IF DB_ID('QuotesLab') IS NOT NULL
BEGIN
    ALTER DATABASE QuotesLab SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE QuotesLab;
END
GO

CREATE DATABASE QuotesLab;
GO

USE QuotesLab;
GO

/* Filtered indexes can only be created -- and only be *used* -- by a connection
   with these two options ON. sqlcmd does not guarantee them, so every script in
   this lab sets them explicitly rather than inheriting whatever the client
   happens to send. A plan that silently ignores IX_Quote_Author_CreatedAt is a
   confusing thing to debug. */
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE SCHEMA app AUTHORIZATION dbo;
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* ---------------------------------------------------------------------------
   Author
   BirthYear is nullable on purpose: for several historical figures the year is
   genuinely unknown, and those NULLs are what make the difference between
   INNER and LEFT joins visible further down. Negative years are BCE.
   --------------------------------------------------------------------------- */
CREATE TABLE app.Author
(
    AuthorId    int           IDENTITY(1,1) NOT NULL,
    FullName    nvarchar(200) NOT NULL,
    Nationality nvarchar(60)  NULL,
    BirthYear   int           NULL,
    CONSTRAINT PK_Author PRIMARY KEY CLUSTERED (AuthorId),
    CONSTRAINT UQ_Author_FullName UNIQUE (FullName)
);
GO

/* ---------------------------------------------------------------------------
   Category — a tree. ParentCategoryId points back at this same table, and a
   NULL parent marks a root. This is the only structure in the database that
   cannot be traversed to arbitrary depth without recursion.
   --------------------------------------------------------------------------- */
CREATE TABLE app.Category
(
    CategoryId       int           IDENTITY(1,1) NOT NULL,
    Name             nvarchar(80)  NOT NULL,
    ParentCategoryId int           NULL,
    CONSTRAINT PK_Category PRIMARY KEY CLUSTERED (CategoryId),
    CONSTRAINT UQ_Category_Name UNIQUE (Name),
    CONSTRAINT FK_Category_Parent
        FOREIGN KEY (ParentCategoryId) REFERENCES app.Category (CategoryId),
    CONSTRAINT CK_Category_NotOwnParent CHECK (ParentCategoryId <> CategoryId)
);
GO

CREATE NONCLUSTERED INDEX IX_Category_ParentCategoryId
    ON app.Category (ParentCategoryId);
GO

/* ---------------------------------------------------------------------------
   Quote
   CategoryId is nullable: an uncategorised quote is a legitimate state, and it
   is the case that a careless INNER JOIN to Category silently deletes from the
   report.
   --------------------------------------------------------------------------- */
CREATE TABLE app.Quote
(
    QuoteId    int            IDENTITY(1,1) NOT NULL,
    AuthorId   int            NOT NULL,
    CategoryId int            NULL,
    QuoteText  nvarchar(1000) NOT NULL,
    CreatedAt  datetime2(0)   NOT NULL,
    IsDeleted  bit            NOT NULL CONSTRAINT DF_Quote_IsDeleted DEFAULT (0),
    CONSTRAINT PK_Quote PRIMARY KEY CLUSTERED (QuoteId),
    CONSTRAINT FK_Quote_Author
        FOREIGN KEY (AuthorId) REFERENCES app.Author (AuthorId),
    CONSTRAINT FK_Quote_Category
        FOREIGN KEY (CategoryId) REFERENCES app.Category (CategoryId),
    CONSTRAINT CK_Quote_TextLength CHECK (LEN(QuoteText) BETWEEN 1 AND 1000)
);
GO

/* The index the Day-7 summary query is written for. Keyed on AuthorId then
   CreatedAt DESC so the per-author "latest" row is the first row of each key
   range -- no sort needed for the ROW_NUMBER window. QuoteId DESC is in the key
   rather than the INCLUDE list because it is the tiebreaker in that same ORDER
   BY, and a tiebreaker that is not in the index order still forces a sort.
   Filtered on IsDeleted = 0 because every query in this lab filters that way
   and soft-deleted rows are dead weight in the index. */
CREATE NONCLUSTERED INDEX IX_Quote_Author_CreatedAt
    ON app.Quote (AuthorId, CreatedAt DESC, QuoteId DESC)
    INCLUDE (QuoteText, CategoryId)
    WHERE IsDeleted = 0;
GO

CREATE NONCLUSTERED INDEX IX_Quote_CategoryId
    ON app.Quote (CategoryId);
GO

/* ---------------------------------------------------------------------------
   Tag / QuoteTag — a plain many-to-many, so the join exercises have a case
   where row multiplication is correct rather than a bug.
   --------------------------------------------------------------------------- */
CREATE TABLE app.Tag
(
    TagId int          IDENTITY(1,1) NOT NULL,
    Name  nvarchar(40) NOT NULL,
    CONSTRAINT PK_Tag PRIMARY KEY CLUSTERED (TagId),
    CONSTRAINT UQ_Tag_Name UNIQUE (Name)
);
GO

CREATE TABLE app.QuoteTag
(
    QuoteId int NOT NULL,
    TagId   int NOT NULL,
    CONSTRAINT PK_QuoteTag PRIMARY KEY CLUSTERED (QuoteId, TagId),
    CONSTRAINT FK_QuoteTag_Quote
        FOREIGN KEY (QuoteId) REFERENCES app.Quote (QuoteId) ON DELETE CASCADE,
    CONSTRAINT FK_QuoteTag_Tag
        FOREIGN KEY (TagId) REFERENCES app.Tag (TagId) ON DELETE CASCADE
);
GO

/* Covering the other direction of the many-to-many, so "which quotes carry
   this tag" is an index seek rather than a scan of the clustered PK. */
CREATE NONCLUSTERED INDEX IX_QuoteTag_TagId_QuoteId
    ON app.QuoteTag (TagId, QuoteId);
GO

/* ---------------------------------------------------------------------------
   AppUser / Collection / CollectionItem — the Week-1 Collections aggregate,
   flattened into tables. Present so the join exercises have a second, deeper
   chain to walk (user -> collection -> item -> quote -> author).
   --------------------------------------------------------------------------- */
CREATE TABLE app.AppUser
(
    UserId      int           IDENTITY(1,1) NOT NULL,
    Email       nvarchar(256) NOT NULL,
    DisplayName nvarchar(100) NOT NULL,
    CONSTRAINT PK_AppUser PRIMARY KEY CLUSTERED (UserId),
    CONSTRAINT UQ_AppUser_Email UNIQUE (Email)
);
GO

CREATE TABLE app.Collection
(
    CollectionId int          IDENTITY(1,1) NOT NULL,
    OwnerUserId  int          NOT NULL,
    Name         nvarchar(80) NOT NULL,
    CONSTRAINT PK_Collection PRIMARY KEY CLUSTERED (CollectionId),
    CONSTRAINT FK_Collection_Owner
        FOREIGN KEY (OwnerUserId) REFERENCES app.AppUser (UserId),
    CONSTRAINT UQ_Collection_Owner_Name UNIQUE (OwnerUserId, Name)
);
GO

CREATE TABLE app.CollectionItem
(
    CollectionId int          NOT NULL,
    QuoteId      int          NOT NULL,
    AddedAt      datetime2(0) NOT NULL,
    CONSTRAINT PK_CollectionItem PRIMARY KEY CLUSTERED (CollectionId, QuoteId),
    CONSTRAINT FK_CollectionItem_Collection
        FOREIGN KEY (CollectionId) REFERENCES app.Collection (CollectionId) ON DELETE CASCADE,
    CONSTRAINT FK_CollectionItem_Quote
        FOREIGN KEY (QuoteId) REFERENCES app.Quote (QuoteId)
);
GO

CREATE NONCLUSTERED INDEX IX_CollectionItem_QuoteId
    ON app.CollectionItem (QuoteId);
GO

PRINT 'QuotesLab schema created.';
GO
