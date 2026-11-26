-- Selects what is wrong after the whole ladder: the idempotency record is gone, lost its stored response, or LockId is not NULL.
SELECT 'idempotency record lost'
WHERE NOT EXISTS (SELECT 1 FROM [platform].[IdempotencyKeys] WHERE [Scope] = N'upgrade-ladder' AND [Key] = N'initial-platform-key');

SELECT 'idempotency record changed'
FROM [platform].[IdempotencyKeys]
WHERE [Scope] = N'upgrade-ladder' AND [Key] = N'initial-platform-key'
  AND ([StatusCode] <> 201 OR [ContentType] <> N'application/json' OR [ResponseBody] <> 0x7B226F6B223A747275657D
       OR [Location] <> N'/api/upgrade-ladder/1' OR [LockId] IS NOT NULL);
