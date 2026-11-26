-- Audit after CreateAuditSchema: one audit trail row for an entity no module owns. Id is an identity column, so the verify script names the
-- row by its change set. Plain T-SQL, no GO.
INSERT INTO [audit].[AuditTrails] ([ChangeSetId], [OccurredAt], [UserId], [SessionId], [Module], [EntityType], [EntityId], [Action], [Changes], [TraceId])
VALUES ('c1000000-0000-0000-0000-000000000001', '2026-01-02T03:04:05.123', 'c1000000-0000-0000-0000-000000000002', NULL,
    N'UpgradeLadder', N'UpgradeEntity', N'upgrade-entity-1', 'Updated', N'{"Name":{"Old":"before","New":"after"}}', N'upgrade-ladder-trace');
