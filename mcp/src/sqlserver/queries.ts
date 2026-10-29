/**
 * The only SQL this server ever runs. Each statement is fixed text with typed parameters; nothing a
 * caller sends is concatenated into SQL except a database name that was matched against the
 * allow-list and bracket-quoted.
 */

/** Waits that are idle background noise rather than signs of trouble. */
const BENIGN_WAITS = [
  "SLEEP%", "BROKER%", "XE%", "SQLTRACE%", "CLR%", "HADR%", "FT_%", "LAZYWRITER%", "CHECKPOINT%", "REQUEST_FOR_DEADLOCK%",
  "DIRTY_PAGE%", "DISPATCHER%", "LOGMGR_QUEUE", "ONDEMAND%", "SP_SERVER_DIAGNOSTICS%", "WAITFOR", "QDS%", "PWAIT%",
  "SOS_WORK_DISPATCHER", "MEMORY_ALLOCATION_EXT", "PREEMPTIVE_%", "KSOURCE_WAKEUP", "SERVER_IDLE_CHECK", "CXCONSUMER",
  "VDI_CLIENT_OTHER", "PARALLEL_REDO%", "WAIT_XTP%", "SOS_SCHEDULER_YIELD",
].map((w) => `wait_type NOT LIKE '${w}'`).join(" AND ");

export const SQL = {
  sessionsByDatabase: `
    SELECT DB_NAME(s.database_id) AS database_name, COUNT(*) AS sessions,
           SUM(CASE WHEN r.session_id IS NOT NULL THEN 1 ELSE 0 END) AS active_requests,
           SUM(CASE WHEN r.blocking_session_id > 0 THEN 1 ELSE 0 END) AS blocked_requests
      FROM sys.dm_exec_sessions s
      LEFT JOIN sys.dm_exec_requests r ON r.session_id = s.session_id
     WHERE s.is_user_process = 1
     GROUP BY s.database_id
     ORDER BY sessions DESC`,

  topWaits: `
    SELECT TOP (8) wait_type, waiting_tasks_count, wait_time_ms
      FROM sys.dm_os_wait_stats
     WHERE waiting_tasks_count > 0 AND ${BENIGN_WAITS}
     ORDER BY wait_time_ms DESC`,

  sysInfo: `
    SELECT cpu_count, physical_memory_kb / 1024 AS physical_memory_mb, committed_kb / 1024 AS committed_mb,
           committed_target_kb / 1024 AS target_mb, sqlserver_start_time,
           CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(40)) AS version, CAST(SERVERPROPERTY('Edition') AS nvarchar(80)) AS edition
      FROM sys.dm_os_sys_info`,

  blocked: `
    SELECT r.session_id, r.blocking_session_id, DB_NAME(r.database_id) AS database_name, r.wait_type, r.wait_time AS wait_ms,
           r.wait_resource, r.status, r.command, LEFT(t.text, 500) AS query_text, s.program_name
      FROM sys.dm_exec_requests r
      JOIN sys.dm_exec_sessions s ON s.session_id = r.session_id
     OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
     WHERE r.blocking_session_id <> 0
     ORDER BY r.wait_time DESC`,

  /** Sessions at the head of a chain: blocking others while not blocked themselves (often idle in a transaction). */
  headBlockers: `
    SELECT s.session_id, s.status, s.open_transaction_count, s.last_request_end_time, s.program_name,
           LEFT(t.text, 500) AS last_query_text
      FROM sys.dm_exec_sessions s
      LEFT JOIN sys.dm_exec_connections c ON c.session_id = s.session_id
     OUTER APPLY sys.dm_exec_sql_text(c.most_recent_sql_handle) t
     WHERE s.session_id IN (SELECT blocking_session_id FROM sys.dm_exec_requests WHERE blocking_session_id <> 0)
       AND s.session_id NOT IN (SELECT session_id FROM sys.dm_exec_requests WHERE blocking_session_id <> 0)`,

  longRequests: `
    SELECT TOP (50) r.session_id, DB_NAME(r.database_id) AS database_name, r.status, r.command,
           r.total_elapsed_time / 1000 AS elapsed_s, r.cpu_time AS cpu_ms, r.logical_reads, r.wait_type,
           LEFT(t.text, 500) AS query_text, s.program_name
      FROM sys.dm_exec_requests r
      JOIN sys.dm_exec_sessions s ON s.session_id = r.session_id AND s.is_user_process = 1
     OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
     WHERE r.session_id <> @@SPID AND r.total_elapsed_time >= @min_ms
     ORDER BY r.total_elapsed_time DESC`,

  deadlocks: `
    WITH d AS (
      SELECT timestamp_utc, CAST(event_data AS xml) AS x
        FROM sys.fn_xe_file_target_read_file('system_health*.xel', NULL, NULL, NULL)
       WHERE object_name = 'xml_deadlock_report' AND timestamp_utc BETWEEN @from AND @to
    )
    SELECT TOP (60) d.timestamp_utc AS time, p.value('@id', 'varchar(60)') AS process, p.value('@spid', 'int') AS spid,
           DB_NAME(p.value('@currentdb', 'int')) AS database_name, p.value('@waitresource', 'varchar(200)') AS wait_resource,
           p.value('@lockMode', 'varchar(20)') AS lock_mode,
           LEFT(LTRIM(p.value('(inputbuf)[1]', 'nvarchar(max)')), 500) AS query_text,
           CASE WHEN p.value('@id', 'varchar(60)') = d.x.value('(event/data/value/deadlock/victim-list/victimProcess/@id)[1]', 'varchar(60)')
                THEN 1 ELSE 0 END AS victim
      FROM d CROSS APPLY d.x.nodes('event/data/value/deadlock/process-list/process') AS n(p)
     ORDER BY d.timestamp_utc DESC`,

  /** Serious errors captured by the built-in system_health session. */
  systemHealthErrors: `
    SELECT TOP (100) e.timestamp_utc AS time,
           e.x.value('(event/data[@name="error_number"]/value)[1]', 'int') AS error_number,
           e.x.value('(event/data[@name="severity"]/value)[1]', 'int') AS severity,
           LEFT(e.x.value('(event/data[@name="message"]/value)[1]', 'nvarchar(max)'), 500) AS message,
           'system_health' AS captured_by
      FROM (SELECT timestamp_utc, CAST(event_data AS xml) AS x
              FROM sys.fn_xe_file_target_read_file('system_health*.xel', NULL, NULL, NULL)
             WHERE object_name = 'error_reported' AND timestamp_utc BETWEEN @from AND @to) e
     ORDER BY e.timestamp_utc DESC`,

  /** Errors of severity 11+ (lock timeouts, conversion failures…) captured by the optional resolveai_errors session. */
  resolveaiSessionErrors: `
    SELECT TOP (100) ev.value('@timestamp', 'datetime2') AS time,
           ev.value('(data[@name="error_number"]/value)[1]', 'int') AS error_number,
           ev.value('(data[@name="severity"]/value)[1]', 'int') AS severity,
           LEFT(ev.value('(data[@name="message"]/value)[1]', 'nvarchar(max)'), 500) AS message,
           ev.value('(action[@name="database_name"]/value)[1]', 'nvarchar(128)') AS database_name,
           'resolveai_errors' AS captured_by
      FROM (SELECT CAST(t.target_data AS xml) AS x
              FROM sys.dm_xe_session_targets t JOIN sys.dm_xe_sessions s ON s.address = t.event_session_address
             WHERE s.name = 'resolveai_errors' AND t.target_name = 'ring_buffer') rb
     CROSS APPLY rb.x.nodes('RingBufferTarget/event') AS n(ev)
     WHERE ev.value('@timestamp', 'datetime2') BETWEEN @from AND @to
     ORDER BY time DESC`,

  databaseState: `
    SELECT name AS database_name, state_desc, recovery_model_desc, log_reuse_wait_desc, is_read_only
      FROM sys.databases WHERE name = @db`,

  /** Runs inside the target database via [db].sys.sp_executesql so FILEPROPERTY sees its files. */
  fileSpace: `
    SELECT name AS file_name, type_desc, CAST(size / 128.0 AS decimal(12, 1)) AS size_mb,
           CAST(FILEPROPERTY(name, 'SpaceUsed') / 128.0 AS decimal(12, 1)) AS used_mb,
           CASE max_size WHEN -1 THEN NULL ELSE CAST(max_size / 128.0 AS decimal(12, 1)) END AS max_mb,
           CASE WHEN is_percent_growth = 1 THEN CONCAT(growth, '%') ELSE CONCAT(growth / 128, ' MB') END AS growth
      FROM sys.database_files`,

  agentStatus: `SELECT status_desc FROM sys.dm_server_services WHERE servicename LIKE 'SQL Server Agent%'`,

  // run_date is yyyymmdd and run_time hhmmss as ints; converted inline because msdb.dbo.agent_datetime
  // needs EXECUTE, which the reader login doesn't have.
  failedJobs: `
    SELECT TOP (50) job_name, step_id, step_name, run_at, message FROM (
      SELECT j.name AS job_name, h.step_id, h.step_name, LEFT(h.message, 500) AS message,
             DATEADD(SECOND, h.run_time / 10000 * 3600 + h.run_time / 100 % 100 * 60 + h.run_time % 100,
                     CAST(CAST(h.run_date AS char(8)) AS datetime2(0))) AS run_at
        FROM msdb.dbo.sysjobhistory h JOIN msdb.dbo.sysjobs_view j ON j.job_id = h.job_id
       WHERE h.run_status = 0 AND h.run_date > 0) f
     WHERE run_at BETWEEN @from AND @to
     ORDER BY run_at DESC`,
} as const;
