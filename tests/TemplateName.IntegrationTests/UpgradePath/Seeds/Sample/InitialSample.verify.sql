-- Selects what is wrong after the whole ladder: the leave request is gone or lost its dates, its status, its approver or its reason.
SELECT 'leave request lost' WHERE NOT EXISTS (SELECT 1 FROM [sample].[LeaveRequests] WHERE [Id] = 'e1000000-0000-0000-0000-000000000001');

SELECT 'leave request changed'
FROM [sample].[LeaveRequests]
WHERE [Id] = 'e1000000-0000-0000-0000-000000000001'
  AND ([EmployeeId] <> 'e1000000-0000-0000-0000-000000000002' OR [StartDate] <> '2026-05-04' OR [EndDate] <> '2026-05-06' OR [Status] <> 2
       OR [ApproverId] <> 'e1000000-0000-0000-0000-000000000003' OR [Reason] <> N'Upgrade ladder leave' OR [IsDeleted] <> 0);
