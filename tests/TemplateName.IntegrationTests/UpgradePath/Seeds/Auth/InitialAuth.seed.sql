-- Auth after InitialAuth: a role, a user and the user's role. Fixed ids and dates, so InitialAuth.verify.sql names exact rows.
-- Plain T-SQL, no GO (the runner sends the file as one batch).
INSERT INTO [auth].[Roles] ([Id], [Name], [NormalizedName], [Description], [IsSystem], [CreatedAt], [IsDeleted])
VALUES ('a1000000-0000-0000-0000-000000000001', N'Seeded role', N'SEEDED ROLE', N'A role written by the upgrade-path ladder', 0,
    '2026-01-02T03:04:05.123', 0);

INSERT INTO [auth].[Users] ([Id], [Email], [NormalizedEmail], [EmailConfirmed], [PasswordHash], [SecurityStamp], [Status], [DisplayName],
    [Locale], [TimeZone], [AccessFailedCount], [CreatedAt], [IsDeleted])
VALUES ('a1000000-0000-0000-0000-000000000002', N'upgrade-initial@example.com', N'UPGRADE-INITIAL@EXAMPLE.COM', 1, N'seed-password-hash',
    N'seed-security-stamp-initial', 0, N'Upgrade Initial', N'en', N'UTC', 0, '2026-01-02T03:04:05.123', 0);

INSERT INTO [auth].[UserRoles] ([UserId], [RoleId], [AssignedBy], [AssignedAt])
VALUES ('a1000000-0000-0000-0000-000000000002', 'a1000000-0000-0000-0000-000000000001', NULL, '2026-01-02T03:04:05.123');
