-- OPTIONAL, and only for the demo: throws away the SchoolFlow database and makes a new empty one,
-- so the site sets itself up from scratch with the current starter forms, teams and rates.
-- EVERYTHING in the SchoolFlow database is deleted. Do not run this once real requests exist.
--
-- Before running: stop the SchoolFlow site in IIS (or put app_offline.htm in the site folder).
-- Replace YOURDOMAIN\WEBSERVERNAME$ (twice) with the same account you used in database.sql the first time.

USE master;
GO
ALTER DATABASE SchoolFlow SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE SchoolFlow;
GO
CREATE DATABASE SchoolFlow;
GO
USE SchoolFlow;
CREATE USER [YOURDOMAIN\WEBSERVERNAME$] FOR LOGIN [YOURDOMAIN\WEBSERVERNAME$];
ALTER ROLE db_owner ADD MEMBER [YOURDOMAIN\WEBSERVERNAME$];
GO
