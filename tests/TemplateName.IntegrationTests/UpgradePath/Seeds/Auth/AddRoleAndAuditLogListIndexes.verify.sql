-- Selects what is wrong after the whole ladder: the open session is gone, or AddMultiFactorAuthentication did not backfill
-- AuthenticatedAt from CreatedAt.
SELECT 'session lost' WHERE NOT EXISTS (SELECT 1 FROM [auth].[UserSessions] WHERE [Id] = 'a2000000-0000-0000-0000-000000000002');

SELECT 'session AuthenticatedAt is not its CreatedAt'
FROM [auth].[UserSessions]
WHERE [Id] = 'a2000000-0000-0000-0000-000000000002' AND [AuthenticatedAt] <> [CreatedAt];
