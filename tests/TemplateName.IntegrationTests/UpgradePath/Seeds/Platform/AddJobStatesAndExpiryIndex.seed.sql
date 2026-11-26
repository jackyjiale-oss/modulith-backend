-- Platform after AddJobStatesAndExpiryIndex (which creates JobStates): the state of a job no host runs, so no runner touches it.
-- Plain T-SQL, no GO.
INSERT INTO [platform].[JobStates] ([Name], [NextRunAt], [LockedUntil], [LastStartedAt], [LastFinishedAt], [LastStatus], [LastError], [LastProcessed])
VALUES (N'upgrade-ladder-job', '2030-01-02T03:04:05.123', NULL, '2026-01-02T03:04:05.123', '2026-01-02T03:04:06.123', N'Succeeded', NULL, 7);
