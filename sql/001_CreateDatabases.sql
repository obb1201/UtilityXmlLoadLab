-- 001: Create the two databases. Safe to run more than once.
-- SapStaging: inbox, staging, evidence. SapTarget: final tables.
USE master;
GO

IF DB_ID(N'SapStaging') IS NULL
BEGIN
    CREATE DATABASE SapStaging;
END;
GO

IF DB_ID(N'SapTarget') IS NULL
BEGIN
    CREATE DATABASE SapTarget;
END;
GO

-- A one-time load: SIMPLE recovery keeps the log small. Compatibility 160 = SQL Server 2022.
ALTER DATABASE SapStaging SET RECOVERY SIMPLE;
ALTER DATABASE SapTarget SET RECOVERY SIMPLE;
GO
ALTER DATABASE SapStaging SET COMPATIBILITY_LEVEL = 160;
ALTER DATABASE SapTarget SET COMPATIBILITY_LEVEL = 160;
GO
