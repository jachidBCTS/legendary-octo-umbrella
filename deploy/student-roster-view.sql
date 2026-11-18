-- Connects Bergen Forms to the student roster on your SQL Server (run once, in SQL Server Management Studio).
-- Bergen Forms reads ONLY this view, and only these six columns: StudentId, FirstName, LastName, School,
-- Grade, Academy. Nothing is copied: each search is a live, read-only query.
--
-- Fill in the four things marked  <<< CHANGE  , then run it.

USE [YourStudentDatabase];          -- <<< CHANGE: the database that holds the roster
GO

-- 1. The view. Map YOUR table and column names on the right-hand side of each AS.
CREATE OR ALTER VIEW dbo.BergenFormsStudents
AS
SELECT
    CAST(s.student_id   AS nvarchar(40))  AS StudentId,   -- <<< CHANGE: your table's column names
    CAST(s.first_name   AS nvarchar(100)) AS FirstName,
    CAST(s.last_name    AS nvarchar(100)) AS LastName,
    CAST(s.school_name  AS nvarchar(200)) AS School,
    CAST(s.grade_level  AS nvarchar(20))  AS Grade,
    CAST(s.academy      AS nvarchar(200)) AS Academy
FROM dbo.Students AS s                                    -- <<< CHANGE: your table (or a join of tables)
WHERE s.is_active = 1;                                    -- <<< CHANGE: keep only current students (or remove this line)
GO

-- 2. Let the web server read the view, and nothing else in this database.
--    BERGEN\WEBSERVER$ is the web server's computer account (the same one used for the Bergen Forms database).
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'BERGEN\WEBSERVER$')
    CREATE USER [BERGEN\WEBSERVER$] FOR LOGIN [BERGEN\WEBSERVER$];
GRANT SELECT ON dbo.BergenFormsStudents TO [BERGEN\WEBSERVER$];
GO

-- 3. Check: this should list a few students with exactly the six columns.
SELECT TOP (5) * FROM dbo.BergenFormsStudents ORDER BY LastName, FirstName;
