-- Selects what is wrong after the whole ladder: the challenge is gone, or AddExternalLogins did not give it the first factor 'pwd'.
SELECT 'MFA challenge lost' WHERE NOT EXISTS (SELECT 1 FROM [auth].[MfaChallenges] WHERE [Id] = 'a3000000-0000-0000-0000-000000000002');

SELECT 'MFA challenge FirstFactor is not pwd'
FROM [auth].[MfaChallenges]
WHERE [Id] = 'a3000000-0000-0000-0000-000000000002' AND [FirstFactor] <> N'pwd';
