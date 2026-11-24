namespace TemplateName.Modules.Auth.Endpoints;

/// <summary>The body of <c>POST /api/v1/auth/me/phone</c>: the number to verify, in international form.</summary>
internal sealed record RequestPhoneCodeRequest(string PhoneNumber)
{
    /// <summary>Only the type name, so a logged or formatted request never carries the number.</summary>
    public override string ToString() => nameof(RequestPhoneCodeRequest);
}

/// <summary>The body of <c>POST /api/v1/auth/me/phone/confirm</c>: the 6-digit code from the text.</summary>
internal sealed record ConfirmPhoneRequest(string Code)
{
    /// <summary>Only the type name, so a logged or formatted request never carries the code.</summary>
    public override string ToString() => nameof(ConfirmPhoneRequest);
}

/// <summary>The body of <c>POST phone/sign-in/send</c>. Prints its type name only.</summary>
internal sealed record SendPhoneSignInCodeRequest(string PhoneNumber)
{
    public override string ToString() => nameof(SendPhoneSignInCodeRequest);
}
