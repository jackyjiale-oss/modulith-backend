-- Selects what is wrong after the whole ladder: the notification or its delivery is gone, the delivery lost its status or its error, or
-- AddDeliveryProviderMessageId gave it a provider message id.
SELECT 'notification lost' WHERE NOT EXISTS (SELECT 1 FROM [notify].[Notifications] WHERE [Id] = 'd1000000-0000-0000-0000-000000000001');

SELECT 'delivery lost' WHERE NOT EXISTS (SELECT 1 FROM [notify].[Deliveries] WHERE [Id] = 'd1000000-0000-0000-0000-000000000004');

SELECT 'delivery changed'
FROM [notify].[Deliveries]
WHERE [Id] = 'd1000000-0000-0000-0000-000000000004'
  AND ([NotificationId] <> 'd1000000-0000-0000-0000-000000000001' OR [Status] <> 2 OR [AttemptCount] <> 5 OR [LastError] <> N'seeded failure'
       OR [Destination] <> N'upgrade-ladder@example.com' OR [ProviderMessageId] IS NOT NULL);
