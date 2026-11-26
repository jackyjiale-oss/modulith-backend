-- Selects what is wrong after the whole ladder: the sent delivery is gone, or it lost its status, its sent time or its provider message id.
SELECT 'sent delivery lost' WHERE NOT EXISTS (SELECT 1 FROM [notify].[Deliveries] WHERE [Id] = 'd2000000-0000-0000-0000-000000000004');

SELECT 'sent delivery changed'
FROM [notify].[Deliveries]
WHERE [Id] = 'd2000000-0000-0000-0000-000000000004'
  AND ([Status] <> 1 OR [Channel] <> 4 OR [SentAt] IS NULL OR [ProviderMessageId] IS NULL OR [ProviderMessageId] <> N'SMupgradeladder0001');
