namespace TemplateName.Modules.Auth.Infrastructure.External;

/// <summary>What <see cref="ExternalNonceGate.TryEnter"/> decided.</summary>
internal enum NonceGateResult
{
    /// <summary>The key was not held: it is now, until its window ends. The caller is the only one to get this for the key in the window.</summary>
    Entered,

    /// <summary>The key is held by an earlier claim whose window has not ended: a replay.</summary>
    Spent,

    /// <summary>The gate holds <see cref="ExternalNonceGate.Capacity"/> live keys and none could be pruned, so the claim is refused (fail closed).</summary>
    Full,
}
