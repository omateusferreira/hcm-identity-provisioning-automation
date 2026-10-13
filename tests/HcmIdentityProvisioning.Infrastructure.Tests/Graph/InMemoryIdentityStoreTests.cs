using FluentAssertions;
using HcmIdentityProvisioning.Domain.Actions;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Enums;
using HcmIdentityProvisioning.Domain.ValueObjects;
using HcmIdentityProvisioning.Infrastructure.Graph;
using Xunit;

namespace HcmIdentityProvisioning.Infrastructure.Tests.Graph;

public class InMemoryIdentityStoreTests
{
    [Fact]
    public async Task ApplyBatchMutationsAsync_CreateAndGroupActions_UpdatesState()
    {
        var store = new InMemoryIdentityStore();
        store.SeedGroup(Guid.NewGuid(), "grp-iam-engineering");

        var emp = new Employee(
            EmployeeId.Create("EMP-001").Value,
            "Carlos Silva",
            EmployeeStatus.Active,
            "Tecnologia",
            "Engenheiro",
            new Dictionary<string, string>()
        );
        var upn = UserPrincipalName.Create("carlos.silva@corp.com").Value;

        var actions = new DeltaAction[]
        {
            new CreateUserAction(emp, upn, "Temp#Pass123456789012")
        };

        await store.ApplyBatchMutationsAsync(actions);

        var users = await store.GetUsersByEmployeeIdsAsync(new[] { emp.Id });
        users.Should().ContainKey(emp.Id);
        users[emp.Id].UserPrincipalName.Value.Should().Be("carlos.silva@corp.com");
    }

    [Fact]
    public async Task SeedGroup_And_GetManagedGroupsAsync_ReturnsStoredGroups()
    {
        var store = new InMemoryIdentityStore();
        var gid = Guid.NewGuid();
        store.SeedGroup(gid, "grp-iam-engineering");

        var groups = await store.GetManagedGroupsAsync();

        groups.Should().ContainKey("grp-iam-engineering");
        groups["grp-iam-engineering"].Id.Should().Be(gid);
        groups["grp-iam-engineering"].DisplayName.Should().Be("grp-iam-engineering");
    }

    [Fact]
    public async Task SeedUser_And_GetUsersByEmployeeIdsAsync_ReturnsOnlyMatchedUsers()
    {
        var store = new InMemoryIdentityStore();
        var empId1 = EmployeeId.Create("EMP-100").Value;
        var empId2 = EmployeeId.Create("EMP-200").Value;
        var empId3 = EmployeeId.Create("EMP-300").Value;

        var user1 = new EntraUser(
            Guid.NewGuid(),
            empId1,
            UserPrincipalName.Create("user1@corp.com").Value,
            "User One",
            AccountEnabled: true,
            AssignedGroupIds: new HashSet<Guid>()
        );
        var user2 = new EntraUser(
            Guid.NewGuid(),
            empId2,
            UserPrincipalName.Create("user2@corp.com").Value,
            "User Two",
            AccountEnabled: false,
            AssignedGroupIds: new HashSet<Guid>()
        );

        store.SeedUser(user1);
        store.SeedUser(user2);

        var result = await store.GetUsersByEmployeeIdsAsync(new[] { empId1, empId3 });

        result.Should().HaveCount(1);
        result.Should().ContainKey(empId1);
        result[empId1].DisplayName.Should().Be("User One");
        result.Should().NotContainKey(empId3);
    }

    [Fact]
    public async Task IsUserPrincipalNameAvailableAsync_EvaluatesCorrectly_CaseInsensitive()
    {
        var store = new InMemoryIdentityStore();
        var upn = UserPrincipalName.Create("user@corp.com").Value;

        var availableBefore = await store.IsUserPrincipalNameAvailableAsync(upn);
        availableBefore.Should().BeTrue();

        var user = new EntraUser(
            Guid.NewGuid(),
            EmployeeId.Create("EMP-1").Value,
            upn,
            "User",
            AccountEnabled: true,
            AssignedGroupIds: new HashSet<Guid>()
        );
        store.SeedUser(user);

        var upnUpper = UserPrincipalName.Create("USER@CORP.COM").Value;
        var availableAfter = await store.IsUserPrincipalNameAvailableAsync(upnUpper);
        availableAfter.Should().BeFalse();
    }

    [Fact]
    public async Task ApplyBatchMutationsAsync_UpdateDisplayName_UpdatesUser()
    {
        var store = new InMemoryIdentityStore();
        var graphId = Guid.NewGuid();
        var empId = EmployeeId.Create("EMP-001").Value;

        store.SeedUser(new EntraUser(
            graphId,
            empId,
            UserPrincipalName.Create("carlos@corp.com").Value,
            "Old Name",
            AccountEnabled: true,
            AssignedGroupIds: new HashSet<Guid>()
        ));

        await store.ApplyBatchMutationsAsync(new[]
        {
            new UpdateDisplayNameAction(graphId, "New Name")
        });

        var users = await store.GetUsersByEmployeeIdsAsync(new[] { empId });
        users[empId].DisplayName.Should().Be("New Name");
    }

    [Fact]
    public async Task ApplyBatchMutationsAsync_EnableAndDisableAccount_TogglesAccountEnabled()
    {
        var store = new InMemoryIdentityStore();
        var graphId = Guid.NewGuid();
        var empId = EmployeeId.Create("EMP-001").Value;

        store.SeedUser(new EntraUser(
            graphId,
            empId,
            UserPrincipalName.Create("carlos@corp.com").Value,
            "Carlos",
            AccountEnabled: true,
            AssignedGroupIds: new HashSet<Guid>()
        ));

        // Disable
        await store.ApplyBatchMutationsAsync(new[] { new DisableAccountAction(graphId) });
        var users = await store.GetUsersByEmployeeIdsAsync(new[] { empId });
        users[empId].AccountEnabled.Should().BeFalse();

        // Enable
        await store.ApplyBatchMutationsAsync(new[] { new EnableAccountAction(graphId) });
        users = await store.GetUsersByEmployeeIdsAsync(new[] { empId });
        users[empId].AccountEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task ApplyBatchMutationsAsync_RevokeSessions_CompletesWithoutError()
    {
        var store = new InMemoryIdentityStore();
        var graphId = Guid.NewGuid();

        var act = () => store.ApplyBatchMutationsAsync(new[] { new RevokeSessionsAction(graphId) });
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ApplyBatchMutationsAsync_AddAndRemoveGroupMember_UpdatesGroupSet()
    {
        var store = new InMemoryIdentityStore();
        var graphId = Guid.NewGuid();
        var groupId1 = Guid.NewGuid();
        var groupId2 = Guid.NewGuid();
        var empId = EmployeeId.Create("EMP-001").Value;

        store.SeedUser(new EntraUser(
            graphId,
            empId,
            UserPrincipalName.Create("carlos@corp.com").Value,
            "Carlos",
            AccountEnabled: true,
            AssignedGroupIds: new HashSet<Guid> { groupId1 }
        ));

        await store.ApplyBatchMutationsAsync(new DeltaAction[]
        {
            new AddGroupMemberAction(graphId, groupId2, "grp-iam-engineering"),
            new RemoveGroupMemberAction(graphId, groupId1, "grp-iam-all-staff")
        });

        var users = await store.GetUsersByEmployeeIdsAsync(new[] { empId });
        users[empId].AssignedGroupIds.Should().Contain(groupId2);
        users[empId].AssignedGroupIds.Should().NotContain(groupId1);
    }
}
