using HcmIdentityProvisioning.Application.Models;
using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Application.Services;
using HcmIdentityProvisioning.Domain.Actions;
using HcmIdentityProvisioning.Domain.Policies;
using HcmIdentityProvisioning.Domain.Ports;
using Microsoft.Extensions.Logging;

namespace HcmIdentityProvisioning.Application.UseCases;

public sealed class ReconcileBatchUseCase
{
    private readonly IHcmConnector _connector;
    private readonly IIdentityStore _identityStore;
    private readonly IdentityReconciliationService _reconciler;
    private readonly ICircuitBreaker _circuitBreaker;
    private readonly ICredentialDeliveryService _credentialDelivery;
    private readonly SyncSettings _settings;
    private readonly ILogger<ReconcileBatchUseCase> _logger;

    public ReconcileBatchUseCase(
        IHcmConnector connector,
        IIdentityStore identityStore,
        IdentityReconciliationService reconciler,
        ICircuitBreaker circuitBreaker,
        ICredentialDeliveryService credentialDelivery,
        SyncSettings settings,
        ILogger<ReconcileBatchUseCase> logger)
    {
        _connector = connector;
        _identityStore = identityStore;
        _reconciler = reconciler;
        _circuitBreaker = circuitBreaker;
        _credentialDelivery = credentialDelivery;
        _settings = settings;
        _logger = logger;
    }

    public async Task<SyncReport> ExecuteAsync(CancellationToken ct = default)
    {
        int page = 1;
        bool hasNext = true;
        var warnings = new List<string>();

        int total = 0, created = 0, updated = 0, enabled = 0, disabled = 0, revoked = 0, grpAdded = 0, grpRemoved = 0;
        bool tripped = false;
        string? breakerMsg = null;

        var managedGroups = await _identityStore.GetManagedGroupsAsync(ct);
        var allocatedUpns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenEmployeeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (hasNext)
        {
            var paged = await _connector.GetEmployeesPageAsync(page, _settings.BatchPageSize, ct);
            total += paged.Items.Count;

            var uniqueEmployees = paged.Items
                .Where(emp => seenEmployeeIds.Add(emp.Id.Value))
                .ToList();

            if (uniqueEmployees.Count == 0 && paged.Items.Count > 0)
            {
                _logger.LogWarning(
                    "Potential cycle detected in HCM connector pagination: all {Count} employees on page {Page} were previously seen. Halting pagination.",
                    paged.Items.Count, page);
                break;
            }

            var existingUsers = await _identityStore.GetUsersByEmployeeIdsAsync(uniqueEmployees.Select(e => e.Id), ct);
            var batchActions = new List<DeltaAction>();

            foreach (var emp in uniqueEmployees)
            {
                existingUsers.TryGetValue(emp.Id, out var existing);
                var actions = await _reconciler.ReconcileEmployeeAsync(
                    emp,
                    existing,
                    _settings.TenantDomain,
                    managedGroups,
                    async (upn, cToken) => !allocatedUpns.Contains(upn.Value) && await _identityStore.IsUserPrincipalNameAvailableAsync(upn, cToken),
                    w => warnings.Add(w),
                    ct
                );

                foreach (var action in actions)
                {
                    if (action is CreateUserAction create)
                    {
                        allocatedUpns.Add(create.UserPrincipalName.Value);
                    }
                }

                batchActions.AddRange(actions);
            }

            if (!tripped && _circuitBreaker.ShouldTrip(uniqueEmployees.Count, batchActions, out var reason))
            {
                tripped = true;
                breakerMsg = reason;
                _logger.LogCritical("CIRCUIT BREAKER TRIPPED: {Reason}", reason);
            }
            else if (tripped)
            {
                _logger.LogWarning("CIRCUIT BREAKER: Suppressing destructive actions on page {Page} due to prior trip", page);
            }

            if (tripped)
            {
                if (_settings.HaltAllOperationsOnTrip)
                {
                    break;
                }

                batchActions.RemoveAll(a => a is DisableAccountAction or RevokeSessionsAction);
            }

            if (batchActions.Count > 0)
            {
                await _identityStore.ApplyBatchMutationsAsync(batchActions, ct);

                foreach (var action in batchActions)
                {
                    switch (action)
                    {
                        case CreateUserAction c:
                            created++;
                            await _credentialDelivery.DeliverInitialCredentialsAsync(c.Employee, c.UserPrincipalName, c.TemporaryPassword, ct);
                            break;
                        case UpdateDisplayNameAction: updated++; break;
                        case EnableAccountAction: enabled++; break;
                        case DisableAccountAction: disabled++; break;
                        case RevokeSessionsAction: revoked++; break;
                        case AddGroupMemberAction: grpAdded++; break;
                        case RemoveGroupMemberAction: grpRemoved++; break;
                    }
                }
            }

            hasNext = paged.HasNextPage;
            page++;
        }

        return new SyncReport(
            total, created, updated, enabled, disabled, revoked, grpAdded, grpRemoved,
            tripped, breakerMsg, warnings
        );
    }
}
