-- Platform after InitialPlatform: a finished idempotency record (a stored 201 response), before AddIdempotencyLockId adds LockId.
-- Plain T-SQL, no GO (the runner sends the file as one batch). The expiry is far ahead so the cleanup job never deletes the row.
INSERT INTO [platform].[IdempotencyKeys] ([Scope], [Key], [RequestHash], [StatusCode], [ContentType], [ResponseBody], [Location], [CreatedAt], [ExpiresAt])
VALUES (N'upgrade-ladder', N'initial-platform-key',
    0x3030303030303030303030303030303030303030303030303030303030303031, 201, N'application/json',
    0x7B226F6B223A747275657D, N'/api/upgrade-ladder/1', '2026-01-02T03:04:05.123', '2030-01-02T03:04:05.123');
