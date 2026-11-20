-- Lets Greenlight read the student roster and the staff list on your SQL Server (run once, in SQL Server
-- Management Studio). From ActiveStudents it reads student number, first name, last name, school (Location),
-- grade, academy and district (district only decides who may see a student). From ActiveStaff it reads where
-- each person works (Location, DistrictShort), their name, title, department and work email. A request
-- stores nothing about a student but the student number.

USE [ThingsOfBergen];
GO

-- The view dbo.ActiveStudents already exists (StudentNumber, FirstName, LastName, District, Location,
-- [Academy or Major], Grade). Give the web server's computer account read access to it, and nothing else.
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'BERGEN\WEBSERVER$')   -- <<< CHANGE if the account name differs
    CREATE USER [BERGEN\WEBSERVER$] FOR LOGIN [BERGEN\WEBSERVER$];
GRANT SELECT ON dbo.ActiveStudents TO [BERGEN\WEBSERVER$];
GRANT SELECT ON dbo.ActiveStaff TO [BERGEN\WEBSERVER$];
GO

-- The two views write places differently (e.g. staff DistrictShort "BCTS" vs the students' District).
-- Run these and send the results, so the matching list in the settings file can be filled in:
SELECT DISTINCT DistrictShort, Location FROM dbo.ActiveStaff ORDER BY DistrictShort, Location;
SELECT DISTINCT District, Location FROM dbo.ActiveStudents ORDER BY District, Location;

-- Check: a few students, with the columns Greenlight uses.
SELECT TOP (5) StudentNumber, FirstName, LastName, Location, Grade, [Academy or Major]
FROM dbo.ActiveStudents ORDER BY LastName, FirstName;

-- Then, in X:\wwwroot\SchoolFlow\appsettings.Production.json, add (and restart the site):
--   "Students": {
--     "ConnectionString": "Server=YOUR-SQL-SERVER;Database=ThingsOfBergen;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Application Name=Greenlight",
--     "View": "dbo.ActiveStudents",
--     "Columns": { "StudentId": "StudentNumber", "School": "Location", "Academy": "Academy or Major", "District": "District" },
--     "Match": { "Districts": { ... }, "Locations": { ... } }      (filled in from the two lists above)
--   },
--   "Staff": { "View": "dbo.ActiveStaff" },     (uses the same connection as Students)
-- and use Admin > Student access > Check the connection.
