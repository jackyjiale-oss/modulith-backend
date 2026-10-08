namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>A confirmed user the harness created and signed in (see <see cref="IntegrationTestBase"/>).</summary>
/// <param name="UserId">The user's id, the token's <c>sub</c>.</param>
/// <param name="Email">The user's unique email address.</param>
/// <param name="Password">The user's password; its hash is stored.</param>
/// <param name="AccessToken">A valid access token for the session, minted by the host's own issuer.</param>
/// <param name="SessionId">The session the token belongs to, the token's <c>sid</c>; it holds one refresh token.</param>
public sealed record SignedInUser(Guid UserId, string Email, string Password, string AccessToken, Guid SessionId);
