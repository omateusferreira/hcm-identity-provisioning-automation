namespace HcmIdentityProvisioning.Application.UseCases;

using HcmIdentityProvisioning.Application.Models;
using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Domain.Ports;
using Microsoft.Extensions.Logging;

public sealed class EnsureManagedGroupsUseCase
{
    private readonly IRulesEngine _rulesEngine;
    private readonly IIdentityStore _identityStore;
    private readonly SyncSettings _settings;
    private readonly ILogger<EnsureManagedGroupsUseCase> _logger;

    public EnsureManagedGroupsUseCase(
        IRulesEngine rulesEngine,
        IIdentityStore identityStore,
        SyncSettings settings,
        ILogger<EnsureManagedGroupsUseCase> logger)
    {
        _rulesEngine = rulesEngine ?? throw new ArgumentNullException(nameof(rulesEngine));
        _identityStore = identityStore ?? throw new ArgumentNullException(nameof(identityStore));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<EnsureGroupsReport> ExecuteAsync(bool dryRun = false, CancellationToken ct = default)
    {
        _logger.LogInformation("Starting EnsureManagedGroups execution (DryRun: {DryRun})", dryRun);

        var declaredGroups = _rulesEngine.GetDeclaredGroupNames();
        var prefix = _settings.ManagedGroupPrefix ?? "grp-iam-";

        // Strictly filter to managed prefix scope
        var managedDeclaredGroups = declaredGroups
            .Where(g => g.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var existingGroupsMap = await _identityStore.GetManagedGroupsAsync(ct);

        var existingList = new List<string>();
        var missingList = new List<string>();

        foreach (var groupName in managedDeclaredGroups)
        {
            if (existingGroupsMap.ContainsKey(groupName))
            {
                existingList.Add(groupName);
            }
            else
            {
                missingList.Add(groupName);
            }
        }

        _logger.LogInformation(
            "Found {TotalDeclared} declared groups: {ExistingCount} existing, {MissingCount} missing",
            managedDeclaredGroups.Count, existingList.Count, missingList.Count);

        var createdList = new List<string>();

        if (!dryRun && missingList.Count > 0)
        {
            foreach (var missingGroup in missingList)
            {
                _logger.LogInformation("Creating missing managed group: {GroupName}", missingGroup);
                var created = await _identityStore.CreateManagedGroupAsync(
                    missingGroup,
                    $"Managed security group created automatically by HCM Identity Provisioning Engine for rule: {missingGroup}",
                    ct);
                createdList.Add(created.DisplayName);
            }
        }

        return new EnsureGroupsReport(
            existingList,
            missingList,
            createdList,
            dryRun
        );
    }
}
