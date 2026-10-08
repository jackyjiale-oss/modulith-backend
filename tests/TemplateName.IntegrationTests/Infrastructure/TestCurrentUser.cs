using TemplateName.Application.Common.Identity;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>The current user for integration tests: whoever the test sets in <see cref="UserId"/>, or anonymous when it is null.</summary>
public sealed class TestCurrentUser : ICurrentUser
{
    public Guid? UserId { get; set; }

    public bool IsAuthenticated => UserId.HasValue;
}
