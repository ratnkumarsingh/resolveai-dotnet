/*
  ResolveAI demo database and read-only login for the SQL Server MCP server.
  Run once as an administrator:  sqlcmd -S localhost -E -C -i demo-systems\sqlserver\setup.sql
  Safe to re-run. Undo with teardown.sql.

  resolveai_reader gets only what the db_* tools need:
    VIEW SERVER STATE         DMVs (sessions, requests, waits) and the system_health / ring-buffer XE targets
    db_datareader             in ResolveAIDemo only; it cannot write, change settings or grant anything
    SQLAgentReaderRole        read SQL Agent job history in msdb

  With Windows sign-in (driver msnodesqlv8) the MCP server connects as you and runs every query under
  EXECUTE AS LOGIN = 'resolveai_reader', so SQL Server itself enforces read-only even if you are an admin.
  The login gets a random password nobody knows; for Docker / tedious set your own with
    ALTER LOGIN resolveai_reader WITH PASSWORD = '<strong password>';
  (mixed-mode authentication must be enabled) and put it in .env as MSSQL_PASSWORD.
*/
SET NOCOUNT ON;

IF DB_ID(N'ResolveAIDemo') IS NULL CREATE DATABASE ResolveAIDemo;
GO
USE ResolveAIDemo;
GO
IF OBJECT_ID(N'dbo.Orders') IS NULL
BEGIN
  CREATE TABLE dbo.Orders (
    OrderId      int IDENTITY PRIMARY KEY,
    CustomerRef  nvarchar(40) NOT NULL,
    Amount       decimal(12, 2) NOT NULL,
    Status       nvarchar(20) NOT NULL DEFAULT N'open',
    CreatedAt    datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
    ReconciledAt datetime2 NULL
  );
  INSERT dbo.Orders (CustomerRef, Amount)
  SELECT TOP (5000) CONCAT(N'cust-', ABS(CHECKSUM(NEWID())) % 900 + 100), (ABS(CHECKSUM(NEWID())) % 20000) / 100.0
    FROM sys.all_objects a CROSS JOIN sys.all_objects b;
END;
IF OBJECT_ID(N'dbo.Payments') IS NULL
  CREATE TABLE dbo.Payments (PaymentId int IDENTITY PRIMARY KEY, OrderId int NOT NULL, Amount decimal(12, 2) NOT NULL);
GO

USE master;
GO
IF SUSER_ID(N'resolveai_reader') IS NULL
BEGIN
  DECLARE @pw nvarchar(128) = CONCAT(NEWID(), N'!a', NEWID());
  DECLARE @sql nvarchar(400) = CONCAT(N'CREATE LOGIN resolveai_reader WITH PASSWORD = ', QUOTENAME(@pw, ''''), N', CHECK_POLICY = ON, DEFAULT_DATABASE = master;');
  EXEC (@sql);
END;
GRANT VIEW SERVER STATE TO resolveai_reader;
GO
USE ResolveAIDemo;
IF USER_ID(N'resolveai_reader') IS NULL CREATE USER resolveai_reader FOR LOGIN resolveai_reader;
ALTER ROLE db_datareader ADD MEMBER resolveai_reader;
GO
USE msdb;
IF USER_ID(N'resolveai_reader') IS NULL CREATE USER resolveai_reader FOR LOGIN resolveai_reader;
ALTER ROLE SQLAgentReaderRole ADD MEMBER resolveai_reader;
GO
USE master;
GO

-- Error capture for db_errors: severity 11+ errors (lock timeouts 1222, query timeouts, conversion errors)
-- into a small in-memory ring buffer. The built-in system_health session only records severity 20+.
IF NOT EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE name = N'resolveai_errors')
  CREATE EVENT SESSION resolveai_errors ON SERVER
    ADD EVENT sqlserver.error_reported (ACTION (sqlserver.database_name, sqlserver.client_app_name) WHERE severity >= 11)
    ADD TARGET package0.ring_buffer (SET max_events_limit = 2000)
    WITH (STARTUP_STATE = ON, MAX_DISPATCH_LATENCY = 5 SECONDS);
GO
IF NOT EXISTS (SELECT 1 FROM sys.dm_xe_sessions WHERE name = N'resolveai_errors')
  ALTER EVENT SESSION resolveai_errors ON SERVER STATE = START;
GO
PRINT 'ResolveAIDemo and resolveai_reader are ready.';
