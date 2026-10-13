using HcmIdentityProvisioning.Domain.Actions;

namespace HcmIdentityProvisioning.Application.Models;

public sealed record ChangeSet(IReadOnlyList<DeltaAction> Actions)
{
    public static readonly ChangeSet Empty = new(Array.Empty<DeltaAction>());
    public bool IsEmpty => Actions.Count == 0;
}
