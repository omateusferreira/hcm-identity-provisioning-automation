using HcmIdentityProvisioning.Application.Models;
using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Application.Services;
using HcmIdentityProvisioning.Domain.Actions;
using HcmIdentityProvisioning.Domain.Policies;
using HcmIdentityProvisioning.Domain.Ports;
using Microsoft.Extensions.Logging;

namespace HcmIdentityProvisioning.Application.UseCases;

public sealed class DryRunAuditUseCase
{
    private readonly IHcmConnector _connector;
    private readonly IIdentityStore _identityStore;
    private readonly IdentityReconciliationService _reconciler;
    private readonly ICircuitBreaker _circuitBreaker;
    private readonly SyncSettings _settings;
    private readonly ILogger<DryRunAuditUseCase> _logger;

    public DryRunAuditUseCase(
        IHcmConnector connector,
        IIdentityStore identityStore,
        IdentityReconciliationService reconciler,
        ICircuitBreaker circuitBreaker,
        SyncSettings settings,
        ILogger<DryRunAuditUseCase> logger)
    {
        _connector = connector;
        _identityStore = identityStore;
        _reconciler = reconciler;
        _circuitBreaker = circuitBreaker;
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

        while (hasNext)
        {
            var paged = await _connector.GetEmployeesPageAsync(page, _settings.BatchPageSize, ct);
            total += paged.Items.Count;

            var existingUsers = await _identityStore.GetUsersByEmployeeIdsAsync(paged.Items.Select(e => e.Id), ct);
            var batchActions = new List<DeltaAction>();

            foreach (var emp in paged.Items)
            {
                existingUsers.TryGetValue(emp.Id, out var existing);
                var actions = await _reconciler.ReconcileEmployeeAsync(
                    emp,
                    existing,
                    _settings.TenantDomain,
                    managedGroups,
                    upn => _identityStore.IsUserPrincipalNameAvailableAsync(upn, ct),
                    w => warnings.Add(w),
                    ct
                );
                batchActions.AddRange(actions);
            }

            if (!tripped && _circuitBreaker.ShouldTrip(paged.Items.Count, batchActions, out var reason))
            {
                tripped = true;
                breakerMsg = reason;
                _logger.LogWarning("DRY RUN: CIRCUIT BREAKER WOULD TRIP: {Reason}", reason);
            }

            foreach (var action in batchActions)
            {
                switch (action)
                {
                    case CreateUserAction: created++; break;
                    case UpdateDisplayNameAction: updated++; break;
                    case EnableAccountAction: enabled++; break;
                    case DisableAccountAction: disabled++; break;
                    case RevokeSessionsAction: revoked++; break;
                    case AddGroupMemberAction: grpAdded++; break;
                    case RemoveGroupMemberAction: grpRemoved++; break;
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
