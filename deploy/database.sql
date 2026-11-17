-- Run on your SQL Server as a sysadmin. Creates an empty database for SchoolFlow and lets
-- the web site's Windows account use it. SchoolFlow creates its own tables the first time it starts.
--
-- Replace YOURDOMAIN\WEBSERVERNAME$ below with the account the IIS app pool runs as:
--   * SQL Server on a DIFFERENT machine from IIS (app pool left as ApplicationPoolIdentity):
--       the web server's computer account, e.g.  YOURDOMAIN\WEBSERVERNAME$
--   * SQL Server on the SAME machine as IIS:      IIS APPPOOL\SchoolFlow
--   * A domain service account, if you set one on the app pool:  YOURDOMAIN\svc-schoolflow

CREATE DATABASE SchoolFlow;
GO
CREATE LOGIN [YOURDOMAIN\WEBSERVERNAME$] FROM WINDOWS;
GO
USE SchoolFlow;
CREATE USER [YOURDOMAIN\WEBSERVERNAME$] FOR LOGIN [YOURDOMAIN\WEBSERVERNAME$];
ALTER ROLE db_owner ADD MEMBER [YOURDOMAIN\WEBSERVERNAME$];
GO
