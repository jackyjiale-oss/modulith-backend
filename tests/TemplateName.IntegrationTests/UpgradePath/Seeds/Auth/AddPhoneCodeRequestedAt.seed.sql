-- Auth after AddPhoneCodeRequestedAt: a user and a phone verification code, before AddPhoneCodePurpose adds PhoneVerificationCodes.Purpose.
-- Plain T-SQL, no GO.
INSERT INTO [auth].[Users] ([Id], [Email], [NormalizedEmail], [EmailConfirmed], [PasswordHash], [SecurityStamp], [Status], [DisplayName],
    [Locale], [TimeZone], [AccessFailedCount], [CreatedAt], [IsDeleted])
VALUES ('a4000000-0000-0000-0000-000000000001', N'upgrade-phone@example.com', N'UPGRADE-PHONE@EXAMPLE.COM', 1, N'seed-password-hash',
    N'seed-security-stamp-phone', 0, N'Upgrade Phone', N'en', N'UTC', 0, '2026-04-05T06:07:08.123', 0);

INSERT INTO [auth].[PhoneVerificationCodes] ([Id], [UserId], [PhoneNumber], [CodeHash], [Salt], [CreatedAt], [ExpiresAt], [AttemptCount])
VALUES ('a4000000-0000-0000-0000-000000000002', 'a4000000-0000-0000-0000-000000000001', N'+60123456789',
    0x3030303030303030303030303030303030303030303030303030303030303032, 0x30303030303030303030303030303033,
    '2026-04-05T06:07:08.123', '2026-04-05T06:12:08.123', 0);
