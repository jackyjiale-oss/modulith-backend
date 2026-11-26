-- Auth after AddRoleAndAuditLogListIndexes: a user and an open session, before AddMultiFactorAuthentication adds UserSessions.AuthenticatedAt.
-- Plain T-SQL, no GO.
INSERT INTO [auth].[Users] ([Id], [Email], [NormalizedEmail], [EmailConfirmed], [PasswordHash], [SecurityStamp], [Status], [DisplayName],
    [Locale], [TimeZone], [AccessFailedCount], [CreatedAt], [IsDeleted])
VALUES ('a2000000-0000-0000-0000-000000000001', N'upgrade-session@example.com', N'UPGRADE-SESSION@EXAMPLE.COM', 1, N'seed-password-hash',
    N'seed-security-stamp-session', 0, N'Upgrade Session', N'en', N'UTC', 0, '2026-02-03T04:05:06.123', 0);

INSERT INTO [auth].[UserSessions] ([Id], [UserId], [AuthMethods], [DeviceName], [SecurityStamp], [CreatedAt], [LastSeenAt], [ExpiresAt])
VALUES ('a2000000-0000-0000-0000-000000000002', 'a2000000-0000-0000-0000-000000000001', N'pwd', N'Upgrade device',
    N'seed-security-stamp-session', '2026-02-03T04:05:06.123', '2026-02-04T04:05:06.123', '2026-05-03T04:05:06.123');
