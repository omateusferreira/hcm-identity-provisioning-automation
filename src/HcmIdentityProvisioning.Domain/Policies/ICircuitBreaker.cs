using HcmIdentityProvisioning.Domain.Actions;

namespace HcmIdentityProvisioning.Domain.Policies;

public interface ICircuitBreaker
{
    bool ShouldTrip(int totalBatchSize, IReadOnlyList<DeltaAction> proposedActions, out string reason);
}
