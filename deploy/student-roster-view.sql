-- Lets Bergen Forms read the student roster on your SQL Server (run once, in SQL Server Management Studio).
-- Bergen Forms reads only six columns of the view: student number, first name, last name, school
-- (Location), grade and academy. It never reads any other column (District is not read), and it stores
-- nothing but the student number on a request: everything else is looked up live when it is shown.

USE [ThingsOfBergen];
GO

-- The view dbo.ActiveStudents already exists (StudentNumber, FirstName, LastName, District, Location,
-- [Academy or Major], Grade). Give the web server's computer account read access to it, and nothing else.
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'BERGEN\WEBSERVER$')   -- <<< CHANGE if the account name differs
    CREATE USER [BERGEN\WEBSERVER$] FOR LOGIN [BERGEN\WEBSERVER$];
GRANT SELECT ON dbo.ActiveStudents TO [BERGEN\WEBSERVER$];
GO

-- Check: a few students, with the columns Bergen Forms uses.
SELECT TOP (5) StudentNumber, FirstName, LastName, Location, Grade, [Academy or Major]
FROM dbo.ActiveStudents ORDER BY LastName, FirstName;

-- Then, in X:\wwwroot\SchoolFlow\appsettings.Production.json, add (and restart the site):
--   "Students": {
--     "ConnectionString": "Server=YOUR-SQL-SERVER;Database=ThingsOfBergen;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Application Name=Bergen Forms",
--     "View": "dbo.ActiveStudents",
--     "Columns": { "StudentId": "StudentNumber", "School": "Location", "Academy": "Academy or Major" }
--   },
-- and use Admin > Student access > Check the connection.
