-- Notifications after InitialNotifications: a notification and its dead-lettered e-mail delivery (a status the delivery worker never picks up),
-- before AddDeliveryProviderMessageId adds Deliveries.ProviderMessageId. Plain T-SQL, no GO.
INSERT INTO [notify].[Notifications] ([Id], [TypeCode], [RecipientUserId], [Priority], [Culture], [Data], [ProtectedData], [CorrelationId], [SourceMessageId], [CreatedAt], [ExpiresAt])
VALUES ('d1000000-0000-0000-0000-000000000001', N'upgrade.ladder', 'd1000000-0000-0000-0000-000000000002', 2, N'en', N'{}', NULL, NULL,
    'd1000000-0000-0000-0000-000000000003', '2026-01-02T03:04:05.123', NULL);

INSERT INTO [notify].[Deliveries] ([Id], [NotificationId], [Channel], [Destination], [Status], [AttemptCount], [NextAttemptAt], [LockedUntil], [LastError],
    [RenderedSubject], [CreatedAt], [SentAt], [DeadLetteredAt], [ExpiresAt])
VALUES ('d1000000-0000-0000-0000-000000000004', 'd1000000-0000-0000-0000-000000000001', 1, N'upgrade-ladder@example.com', 2, 5, NULL, NULL,
    N'seeded failure', N'Upgrade ladder', '2026-01-02T03:04:05.123', NULL, '2026-01-02T04:04:05.123', NULL);
