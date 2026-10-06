namespace HcmIdentityProvisioning.Application.Models;

public sealed record SyncReport(
    int TotalProcessed,
    int CreatedCount,
    int UpdatedCount,
    int EnabledCount,
    int DisabledCount,
    int SessionsRevokedCount,
    int GroupMembershipsAdded,
    int GroupMembershipsRemoved,
    bool CircuitBreakerTripped,
    string? CircuitBreakerMessage,
    IReadOnlyList<string> Warnings
);
