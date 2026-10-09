using Microsoft.AspNetCore.DataProtection;
using TemplateName.Application.Common.Security;

namespace TemplateName.Infrastructure.Common.Security;

/// <summary>Protects secrets with ASP.NET Core Data Protection, whose key ring is shared by every instance (ADR 0017).</summary>
internal sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    /// <summary>Changing it makes every value protected so far unreadable.</summary>
    private const string Purpose = "TemplateName.Secrets.v1";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}
