-- Auth after AddCleanupIndexes: a user and an MFA challenge, before AddExternalLogins adds MfaChallenges.FirstFactor. Plain T-SQL, no GO.
INSERT INTO [auth].[Users] ([Id], [Email], [NormalizedEmail], [EmailConfirmed], [PasswordHash], [SecurityStamp], [Status], [DisplayName],
    [Locale], [TimeZone], [AccessFailedCount], [CreatedAt], [IsDeleted])
VALUES ('a3000000-0000-0000-0000-000000000001', N'upgrade-challenge@example.com', N'UPGRADE-CHALLENGE@EXAMPLE.COM', 1, N'seed-password-hash',
    N'seed-security-stamp-challenge', 0, N'Upgrade Challenge', N'en', N'UTC', 0, '2026-03-04T05:06:07.123', 0);

INSERT INTO [auth].[MfaChallenges] ([Id], [UserId], [TokenHash], [SecurityStamp], [DeviceName], [CreatedAt], [ExpiresAt], [FailedAttempts])
VALUES ('a3000000-0000-0000-0000-000000000002', 'a3000000-0000-0000-0000-000000000001',
    0x3030303030303030303030303030303030303030303030303030303030303031, N'seed-security-stamp-challenge', N'Upgrade device',
    '2026-03-04T05:06:07.123', '2026-03-04T05:11:07.123', 0);
