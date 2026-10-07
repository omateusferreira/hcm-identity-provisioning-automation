using HcmIdentityProvisioning.Domain.Actions;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.ValueObjects;

namespace HcmIdentityProvisioning.Domain.Ports;

public interface IIdentityStore
{
    Task<IReadOnlyDictionary<EmployeeId, EntraUser>> GetUsersByEmployeeIdsAsync(
        IEnumerable<EmployeeId> employeeIds,
        CancellationToken ct = default);

    Task<bool> IsUserPrincipalNameAvailableAsync(
        UserPrincipalName upn,
        CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, ManagedGroup>> GetManagedGroupsAsync(
        CancellationToken ct = default);

    Task ApplyBatchMutationsAsync(
        IEnumerable<DeltaAction> actions,
        CancellationToken ct = default);

    Task<ManagedGroup> CreateManagedGroupAsync(
        string displayName,
        string? description = null,
        CancellationToken ct = default);
}
