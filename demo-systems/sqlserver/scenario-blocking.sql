/*
  Reproduces INC-1046 for real: a long "reconciliation" transaction holds locks on dbo.Orders while
  order placement waits behind it. Needs two terminals (run as an administrator, not as resolveai_reader):

    Terminal 1 (the head blocker, holds locks for 3 minutes):
      sqlcmd -S localhost -E -C -d ResolveAIDemo -i demo-systems\sqlserver\scenario-blocking.sql -v ROLE=reconcile
    Terminal 2 (the blocked "place order" request):
      sqlcmd -S localhost -E -C -d ResolveAIDemo -i demo-systems\sqlserver\scenario-blocking.sql -v ROLE=place_order

  While both run, investigate INC-1046 in ResolveAI: db_blocking shows the chain. Everything rolls back;
  no data is changed. Ctrl+C in terminal 1 ends it early.
*/
SET NOCOUNT ON;
IF '$(ROLE)' = 'reconcile'
BEGIN
  BEGIN TRANSACTION;
  UPDATE dbo.Orders SET ReconciledAt = SYSUTCDATETIME() WHERE Status = N'open';
  PRINT 'Holding locks on dbo.Orders for 180 seconds...';
  WAITFOR DELAY '00:03:00';
  ROLLBACK;
  PRINT 'Rolled back; locks released.';
END
ELSE IF '$(ROLE)' = 'place_order'
BEGIN
  SET LOCK_TIMEOUT 120000;  -- wait up to 2 minutes, then fail with error 1222 like the app did
  BEGIN TRANSACTION;
  UPDATE dbo.Orders SET Status = N'placed' WHERE OrderId = 1;
  ROLLBACK;
  PRINT 'Order update went through (the blocker had finished).';
END
ELSE PRINT 'Pass -v ROLE=reconcile or -v ROLE=place_order';
