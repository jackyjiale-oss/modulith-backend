-- Selects what is wrong after the whole ladder: the job state is gone or lost its last run.
SELECT 'job state lost' WHERE NOT EXISTS (SELECT 1 FROM [platform].[JobStates] WHERE [Name] = N'upgrade-ladder-job');

SELECT 'job state changed'
FROM [platform].[JobStates]
WHERE [Name] = N'upgrade-ladder-job' AND ([LastStatus] <> N'Succeeded' OR [LastProcessed] <> 7 OR [NextRunAt] <> '2030-01-02T03:04:05.123');
