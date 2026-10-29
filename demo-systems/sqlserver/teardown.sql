/* Removes everything setup.sql created:  sqlcmd -S localhost -E -C -i demo-systems\sqlserver\teardown.sql */
SET NOCOUNT ON;
IF EXISTS (SELECT 1 FROM sys.dm_xe_sessions WHERE name = N'resolveai_errors') ALTER EVENT SESSION resolveai_errors ON SERVER STATE = STOP;
IF EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE name = N'resolveai_errors') DROP EVENT SESSION resolveai_errors ON SERVER;
GO
USE msdb;
IF USER_ID(N'resolveai_reader') IS NOT NULL DROP USER resolveai_reader;
GO
USE master;
IF DB_ID(N'ResolveAIDemo') IS NOT NULL
BEGIN
  ALTER DATABASE ResolveAIDemo SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
  DROP DATABASE ResolveAIDemo;
END;
IF SUSER_ID(N'resolveai_reader') IS NOT NULL DROP LOGIN resolveai_reader;
GO
PRINT 'ResolveAI demo objects removed.';
