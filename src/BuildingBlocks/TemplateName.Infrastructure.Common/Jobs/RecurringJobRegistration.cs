namespace TemplateName.Infrastructure.Common.Jobs;

/// <summary>One job type registered with <c>AddRecurringJob</c>; the runner reads them all when it first builds its registry.</summary>
internal sealed record RecurringJobRegistration(Type JobType);
