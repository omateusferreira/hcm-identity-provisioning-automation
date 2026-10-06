using HcmIdentityProvisioning.Domain.Common;

namespace HcmIdentityProvisioning.Domain.ValueObjects;

public sealed record UserPrincipalName
{
    public string Value { get; }
    public string Username { get; }
    public string Domain { get; }

    private UserPrincipalName(string value, string username, string domain)
    {
        Value = value;
        Username = username;
        Domain = domain;
    }

    public static Result<UserPrincipalName, string> Create(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Result<UserPrincipalName, string>.Failure("UPN cannot be null or whitespace.");

        var trimmed = value.Trim().ToLowerInvariant();
        var parts = trimmed.Split('@');
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]) || !parts[1].Contains('.'))
            return Result<UserPrincipalName, string>.Failure($"Invalid UPN format: '{value}'.");

        return Result<UserPrincipalName, string>.Success(new UserPrincipalName(trimmed, parts[0], parts[1]));
    }

    public override string ToString() => Value;
}
