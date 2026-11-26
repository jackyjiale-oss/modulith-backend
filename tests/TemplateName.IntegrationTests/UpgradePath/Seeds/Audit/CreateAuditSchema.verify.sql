-- Selects what is wrong after the whole ladder: the audit trail row is gone or its fields changed.
SELECT 'audit trail lost' WHERE NOT EXISTS (SELECT 1 FROM [audit].[AuditTrails] WHERE [ChangeSetId] = 'c1000000-0000-0000-0000-000000000001');

SELECT 'audit trail changed'
FROM [audit].[AuditTrails]
WHERE [ChangeSetId] = 'c1000000-0000-0000-0000-000000000001'
  AND ([Module] <> N'UpgradeLadder' OR [EntityType] <> N'UpgradeEntity' OR [EntityId] <> N'upgrade-entity-1' OR [Action] <> 'Updated'
       OR [Changes] <> N'{"Name":{"Old":"before","New":"after"}}' OR [UserId] <> 'c1000000-0000-0000-0000-000000000002');
