using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.ValueObjects;

namespace HcmIdentityProvisioning.Domain.Actions;

public abstract record DeltaAction(Guid? TargetGraphId)
{
    public abstract string ActionName { get; }
}

public sealed record CreateUserAction(
    Employee Employee,
    UserPrincipalName UserPrincipalName,
    string TemporaryPassword
) : DeltaAction((Guid?)null)
{
    public override string ActionName => "CreateUser";
}

public sealed record UpdateDisplayNameAction(
    Guid GraphId,
    string NewDisplayName
) : DeltaAction(GraphId)
{
    public override string ActionName => "UpdateDisplayName";
}

public sealed record EnableAccountAction(
    Guid GraphId
) : DeltaAction(GraphId)
{
    public override string ActionName => "EnableAccount";
}

public sealed record DisableAccountAction(
    Guid GraphId
) : DeltaAction(GraphId)
{
    public override string ActionName => "DisableAccount";
}

public sealed record RevokeSessionsAction(
    Guid GraphId
) : DeltaAction(GraphId)
{
    public override string ActionName => "RevokeSessions";
}

public sealed record AddGroupMemberAction(
    Guid GraphId,
    Guid GroupId,
    string GroupName,
    EmployeeId? EmployeeId = null
) : DeltaAction(GraphId)
{
    public override string ActionName => "AddGroupMember";
}

public sealed record RemoveGroupMemberAction(
    Guid GraphId,
    Guid GroupId,
    string GroupName
) : DeltaAction(GraphId)
{
    public override string ActionName => "RemoveGroupMember";
}
