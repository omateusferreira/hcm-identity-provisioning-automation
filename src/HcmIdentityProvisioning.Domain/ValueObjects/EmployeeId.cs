using HcmIdentityProvisioning.Domain.Common;

namespace HcmIdentityProvisioning.Domain.ValueObjects;

public sealed record EmployeeId
{
    public string Value { get; }

    private EmployeeId(string value) => Value = value;

    public static Result<EmployeeId, string> Create(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Result<EmployeeId, string>.Failure("EmployeeId cannot be null or whitespace.");

        return Result<EmployeeId, string>.Success(new EmployeeId(value.Trim()));
    }

    public override string ToString() => Value;
}
