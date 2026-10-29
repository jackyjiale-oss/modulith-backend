namespace TemplateName.SharedKernel.Auditing;

/// <summary>The change one save made to one audited entity.</summary>
/// <param name="EntityType">The CLR type name of the entity without its namespace, for example <c>LeaveRequest</c>.</param>
/// <param name="EntityId">The primary key of the entity as text.</param>
/// <param name="Action">What happened to the entity.</param>
/// <param name="Changes">JSON object text holding the changed properties and their values.</param>
public sealed record EntityChange(string EntityType, string EntityId, EntityChangeAction Action, string Changes);
