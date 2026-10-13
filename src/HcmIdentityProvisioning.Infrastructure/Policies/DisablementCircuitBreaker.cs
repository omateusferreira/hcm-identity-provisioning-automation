using HcmIdentityProvisioning.Domain.Actions;
using HcmIdentityProvisioning.Domain.Policies;

namespace HcmIdentityProvisioning.Infrastructure.Policies;

public sealed class DisablementCircuitBreaker : ICircuitBreaker
{
    public double MaxDisablePercentage { get; }
    public int MaxDisableCount { get; }

    public DisablementCircuitBreaker(double maxDisablePercentage = 10.0, int maxDisableCount = 25)
    {
        MaxDisablePercentage = maxDisablePercentage;
        MaxDisableCount = maxDisableCount;
    }

    public bool ShouldTrip(int totalBatchSize, IReadOnlyList<DeltaAction> proposedActions, out string reason)
    {
        reason = string.Empty;
        if (totalBatchSize <= 0) return false;

        var disableCount = proposedActions.Count(a => a is DisableAccountAction);
        if (disableCount == 0) return false;

        var disableRate = (double)disableCount / totalBatchSize * 100.0;

        if (disableRate > MaxDisablePercentage)
        {
            reason = FormattableString.Invariant($"CRITICAL_AUDIT_BREAKER_TRIPPED: Disablement Rate {disableRate:F2}% exceeds threshold {MaxDisablePercentage:F2}% ({disableCount}/{totalBatchSize} accounts).");
            return true;
        }

        if (disableCount > MaxDisableCount)
        {
            reason = FormattableString.Invariant($"CRITICAL_AUDIT_BREAKER_TRIPPED: Disablement Count {disableCount} exceeds absolute limit {MaxDisableCount}.");
            return true;
        }

        return false;
    }
}
