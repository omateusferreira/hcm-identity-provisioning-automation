using HcmIdentityProvisioning.Domain.ValueObjects;

namespace HcmIdentityProvisioning.Domain.Entities;

public sealed record EntraUser(
    Guid GraphId,
    EmployeeId EmployeeId,
    UserPrincipalName UserPrincipalName,
    string DisplayName,
    bool AccountEnabled,
    IReadOnlySet<Guid> AssignedGroupIds
);
