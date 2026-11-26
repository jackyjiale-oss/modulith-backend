-- Selects what is wrong after the whole ladder; no rows when every row InitialAuth.seed.sql wrote is still there.
SELECT 'role lost' WHERE NOT EXISTS (SELECT 1 FROM [auth].[Roles] WHERE [Id] = 'a1000000-0000-0000-0000-000000000001');

SELECT 'user lost' WHERE NOT EXISTS (SELECT 1 FROM [auth].[Users] WHERE [Id] = 'a1000000-0000-0000-0000-000000000002');

SELECT 'user role lost' WHERE NOT EXISTS (
    SELECT 1 FROM [auth].[UserRoles]
    WHERE [UserId] = 'a1000000-0000-0000-0000-000000000002' AND [RoleId] = 'a1000000-0000-0000-0000-000000000001');
