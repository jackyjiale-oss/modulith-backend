-- Notifications after AddDeliveryProviderMessageId: a notification and its sent text-message delivery that carries a provider message id.
-- Plain T-SQL, no GO.
INSERT INTO [notify].[Notifications] ([Id], [TypeCode], [RecipientUserId], [Priority], [Culture], [Data], [ProtectedData], [CorrelationId], [SourceMessageId], [CreatedAt], [ExpiresAt])
VALUES ('d2000000-0000-0000-0000-000000000001', N'upgrade.ladder.sms', 'd2000000-0000-0000-0000-000000000002', 3, N'en', N'{}', NULL, NULL,
    'd2000000-0000-0000-0000-000000000003', '2026-02-03T04:05:06.123', NULL);

INSERT INTO [notify].[Deliveries] ([Id], [NotificationId], [Channel], [Destination], [Status], [AttemptCount], [NextAttemptAt], [LockedUntil], [LastError],
    [RenderedSubject], [CreatedAt], [SentAt], [DeadLetteredAt], [ExpiresAt], [ProviderMessageId])
VALUES ('d2000000-0000-0000-0000-000000000004', 'd2000000-0000-0000-0000-000000000001', 4, N'+60123456789', 1, 1, NULL, NULL, NULL,
    NULL, '2026-02-03T04:05:06.123', '2026-02-03T04:05:07.123', NULL, NULL, N'SMupgradeladder0001');
