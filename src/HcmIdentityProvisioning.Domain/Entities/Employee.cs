using HcmIdentityProvisioning.Domain.Enums;
using HcmIdentityProvisioning.Domain.ValueObjects;

namespace HcmIdentityProvisioning.Domain.Entities;

public sealed record Employee(
    EmployeeId Id,
    string FullName,
    EmployeeStatus Status,
    string Department,
    string JobTitle,
    IReadOnlyDictionary<string, string> ExtendedAttributes
);
