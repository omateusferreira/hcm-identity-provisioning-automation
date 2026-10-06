using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HcmIdentityProvisioning.Domain.Actions;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Enums;
using HcmIdentityProvisioning.Domain.Policies;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Domain.ValueObjects;

namespace HcmIdentityProvisioning.Application.Services;

public sealed class IdentityReconciliationService
{
    private readonly IRulesEngine _rulesEngine;
    private readonly ISecurePasswordGenerator _passwordGenerator;

    public IdentityReconciliationService(IRulesEngine rulesEngine, ISecurePasswordGenerator passwordGenerator)
    {
        _rulesEngine = rulesEngine;
        _passwordGenerator = passwordGenerator;
    }

    public async Task<IReadOnlyList<DeltaAction>> ReconcileEmployeeAsync(
        Employee employee,
        EntraUser? existingUser,
        string tenantDomain,
        IReadOnlyDictionary<string, ManagedGroup> managedGroups,
        Func<UserPrincipalName, Task<bool>> isUpnAvailable,
        Action<string>? onWarning = null,
        CancellationToken ct = default)
    {
        var actions = new List<DeltaAction>();
        var desiredGroups = await _rulesEngine.EvaluateDesiredGroupsAsync(employee, ct);

        // 1. Joiner Workflow
        if (existingUser is null)
        {
            if (employee.Status != EmployeeStatus.Active)
                return actions;

            var slug = SanitizeNameToSlug(employee.FullName);
            var upn = await ResolveAvailableUpnAsync(slug, tenantDomain, isUpnAvailable);
            var tempPassword = _passwordGenerator.GeneratePassword(24);

            actions.Add(new CreateUserAction(employee, upn, tempPassword));

            foreach (var groupName in desiredGroups)
            {
                if (managedGroups.TryGetValue(groupName, out var group))
                {
                    actions.Add(new AddGroupMemberAction(Guid.Empty, group.Id, group.DisplayName));
                }
                else
                {
                    onWarning?.Invoke($"WARNING_MANAGED_GROUP_NOT_FOUND: Group '{groupName}' was not found in directory.");
                }
            }

            return actions;
        }

        // 2. Leaver Workflow
        if (employee.Status == EmployeeStatus.Inactive)
        {
            if (existingUser.AccountEnabled)
            {
                actions.Add(new DisableAccountAction(existingUser.GraphId));
                actions.Add(new RevokeSessionsAction(existingUser.GraphId));
            }

            foreach (var managedGroup in managedGroups.Values)
            {
                if (existingUser.AssignedGroupIds.Contains(managedGroup.Id))
                {
                    actions.Add(new RemoveGroupMemberAction(existingUser.GraphId, managedGroup.Id, managedGroup.DisplayName));
                }
            }

            return actions;
        }

        // 3. Mover / Existing Active Workflow
        if (!existingUser.AccountEnabled)
        {
            actions.Add(new EnableAccountAction(existingUser.GraphId));
        }

        if (!string.Equals(existingUser.DisplayName, employee.FullName, StringComparison.Ordinal))
        {
            actions.Add(new UpdateDisplayNameAction(existingUser.GraphId, employee.FullName));
        }

        foreach (var groupName in desiredGroups)
        {
            if (managedGroups.TryGetValue(groupName, out var group))
            {
                if (!existingUser.AssignedGroupIds.Contains(group.Id))
                {
                    actions.Add(new AddGroupMemberAction(existingUser.GraphId, group.Id, group.DisplayName));
                }
            }
            else
            {
                onWarning?.Invoke($"WARNING_MANAGED_GROUP_NOT_FOUND: Group '{groupName}' was not found in directory.");
            }
        }

        foreach (var managedGroup in managedGroups.Values)
        {
            if (existingUser.AssignedGroupIds.Contains(managedGroup.Id) && !desiredGroups.Contains(managedGroup.DisplayName))
            {
                actions.Add(new RemoveGroupMemberAction(existingUser.GraphId, managedGroup.Id, managedGroup.DisplayName));
            }
        }

        return actions;
    }

    private static string SanitizeNameToSlug(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            return "user";

        var normalized = fullName.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }

        var clean = sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant()
            .Replace("'", "").Replace("\"", "");
        var parts = Regex.Split(clean, @"\s+").Where(p => !string.IsNullOrWhiteSpace(p)).ToList();

        if (parts.Count == 0) return "user";
        if (parts.Count == 1) return parts[0];
        return $"{parts.First()}.{parts.Last()}";
    }

    private static async Task<UserPrincipalName> ResolveAvailableUpnAsync(
        string slug,
        string domain,
        Func<UserPrincipalName, Task<bool>> isAvailable)
    {
        var baseUpn = UserPrincipalName.Create($"{slug}@{domain}").Value;
        if (await isAvailable(baseUpn)) return baseUpn;

        int counter = 2;
        while (counter < 1000)
        {
            var next = UserPrincipalName.Create($"{slug}{counter}@{domain}").Value;
            if (await isAvailable(next)) return next;
            counter++;
        }

        throw new InvalidOperationException($"Could not allocate available UPN for '{slug}'.");
    }
}
