using HcmIdentityProvisioning.Domain.Common;

namespace HcmIdentityProvisioning.Domain.ValueObjects;

public sealed record EmailAddress
{
    public string Value { get; }

    private EmailAddress(string value) => Value = value;

    public static Result<EmailAddress, string> Create(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Result<EmailAddress, string>.Failure(DomainErrors.EmailAddressInvalidFormat);

        var trimmed = value.Trim().ToLowerInvariant();
        var parts = trimmed.Split('@');
        if (parts.Length != 2 ||
            string.IsNullOrWhiteSpace(parts[0]) ||
            string.IsNullOrWhiteSpace(parts[1]) ||
            !parts[1].Contains('.') ||
            parts[1].StartsWith('.') ||
            parts[1].EndsWith('.'))
        {
            return Result<EmailAddress, string>.Failure(DomainErrors.EmailAddressInvalidFormat);
        }

        return Result<EmailAddress, string>.Success(new EmailAddress(trimmed));
    }

    public override string ToString() => Value;
}
