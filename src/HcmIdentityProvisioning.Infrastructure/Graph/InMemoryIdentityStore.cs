using System.Collections.Concurrent;
using HcmIdentityProvisioning.Domain.Actions;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Domain.ValueObjects;

namespace HcmIdentityProvisioning.Infrastructure.Graph;

public sealed class InMemoryIdentityStore : IIdentityStore
{
    private readonly ConcurrentDictionary<Guid, EntraUser> _usersByGraphId = new();
    private readonly ConcurrentDictionary<string, ManagedGroup> _groupsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _mutationLock = new();

    public void SeedGroup(Guid id, string displayName)
    {
        _groupsByName[displayName] = new ManagedGroup(id, displayName);
    }

    public void SeedUser(EntraUser user)
    {
        _usersByGraphId[user.GraphId] = user;
    }

    public Task<IReadOnlyDictionary<EmployeeId, EntraUser>> GetUsersByEmployeeIdsAsync(
        IEnumerable<EmployeeId> employeeIds,
        CancellationToken ct = default)
    {
        var idSet = new HashSet<string>(employeeIds.Select(e => e.Value));
        var matched = _usersByGraphId.Values
            .Where(u => idSet.Contains(u.EmployeeId.Value))
            .DistinctBy(u => u.EmployeeId)
            .ToDictionary(u => u.EmployeeId, u => u);

        return Task.FromResult<IReadOnlyDictionary<EmployeeId, EntraUser>>(matched);
    }

    public Task<bool> IsUserPrincipalNameAvailableAsync(UserPrincipalName upn, CancellationToken ct = default)
    {
        var exists = _usersByGraphId.Values.Any(u => u.UserPrincipalName.Value.Equals(upn.Value, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(!exists);
    }

    public Task<IReadOnlyDictionary<string, ManagedGroup>> GetManagedGroupsAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyDictionary<string, ManagedGroup>>(_groupsByName);
    }

    public Task ApplyBatchMutationsAsync(IEnumerable<DeltaAction> actions, CancellationToken ct = default)
    {
        lock (_mutationLock)
        {
            Guid? lastCreatedGraphId = null;

            foreach (var action in actions)
            {
                switch (action)
                {
                    case CreateUserAction create:
                        var graphId = Guid.NewGuid();
                        lastCreatedGraphId = graphId;
                        var newUser = new EntraUser(
                            graphId,
                            create.Employee.Id,
                            create.UserPrincipalName,
                            create.Employee.FullName,
                            AccountEnabled: true,
                            AssignedGroupIds: new HashSet<Guid>()
                        );
                        _usersByGraphId[graphId] = newUser;
                        break;

                    case UpdateDisplayNameAction updateName:
                        if (_usersByGraphId.TryGetValue(updateName.GraphId, out var existingForName))
                        {
                            _usersByGraphId[updateName.GraphId] = existingForName with { DisplayName = updateName.NewDisplayName };
                        }
                        break;

                    case EnableAccountAction enable:
                        if (_usersByGraphId.TryGetValue(enable.GraphId, out var existingForEnable))
                        {
                            _usersByGraphId[enable.GraphId] = existingForEnable with { AccountEnabled = true };
                        }
                        break;

                    case DisableAccountAction disable:
                        if (_usersByGraphId.TryGetValue(disable.GraphId, out var existingForDisable))
                        {
                            _usersByGraphId[disable.GraphId] = existingForDisable with { AccountEnabled = false };
                        }
                        break;

                    case RevokeSessionsAction:
                        // Simulated in-memory: no-op
                        break;

                    case AddGroupMemberAction addGroup:
                        var targetAddGraphId = addGroup.GraphId == Guid.Empty && lastCreatedGraphId.HasValue
                            ? lastCreatedGraphId.Value
                            : addGroup.GraphId;

                        if (_usersByGraphId.TryGetValue(targetAddGraphId, out var existingForAddGroup))
                        {
                            var groups = new HashSet<Guid>(existingForAddGroup.AssignedGroupIds) { addGroup.GroupId };
                            _usersByGraphId[targetAddGraphId] = existingForAddGroup with { AssignedGroupIds = groups };
                        }
                        break;

                    case RemoveGroupMemberAction removeGroup:
                        var targetRemoveGraphId = removeGroup.GraphId == Guid.Empty && lastCreatedGraphId.HasValue
                            ? lastCreatedGraphId.Value
                            : removeGroup.GraphId;

                        if (_usersByGraphId.TryGetValue(targetRemoveGraphId, out var existingForRemoveGroup))
                        {
                            var groups = new HashSet<Guid>(existingForRemoveGroup.AssignedGroupIds);
                            groups.Remove(removeGroup.GroupId);
                            _usersByGraphId[targetRemoveGraphId] = existingForRemoveGroup with { AssignedGroupIds = groups };
                        }
                        break;
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task<ManagedGroup> CreateManagedGroupAsync(
        string displayName,
        string? description = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        if (_groupsByName.TryGetValue(displayName, out var existing))
        {
            return Task.FromResult(existing);
        }

        var newGroup = new ManagedGroup(Guid.NewGuid(), displayName);
        _groupsByName[displayName] = newGroup;
        return Task.FromResult(newGroup);
    }
}
