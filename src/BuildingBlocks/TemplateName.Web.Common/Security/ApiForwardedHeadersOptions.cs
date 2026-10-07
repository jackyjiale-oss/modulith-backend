using System.ComponentModel.DataAnnotations;
using System.Net;

namespace TemplateName.Web.Common.Security;

/// <summary>
/// Trusted reverse proxies (section <c>ForwardedHeaders</c>). <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> are honored only
/// from these proxies; with both lists empty, only loopback proxies are trusted.
/// </summary>
internal sealed class ApiForwardedHeadersOptions : IValidatableObject
{
    internal const string SectionName = "ForwardedHeaders";

    /// <summary>IP addresses of trusted proxies, for example <c>10.0.0.5</c>.</summary>
    public string[] KnownProxies { get; set; } = [];

    /// <summary>CIDR networks of trusted proxies, for example <c>10.0.0.0/24</c>.</summary>
    public string[] KnownNetworks { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var proxy in KnownProxies.Where(proxy => !IPAddress.TryParse(proxy, out _)))
        {
            yield return new ValidationResult($"'{proxy}' is not a valid IP address.", [nameof(KnownProxies)]);
        }

        foreach (var network in KnownNetworks.Where(network => !IPNetwork.TryParse(network, out _)))
        {
            yield return new ValidationResult($"'{network}' is not a valid CIDR network.", [nameof(KnownNetworks)]);
        }
    }
}
