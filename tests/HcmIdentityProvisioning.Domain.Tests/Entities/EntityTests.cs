using FluentAssertions;
using HcmIdentityProvisioning.Domain.Actions;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Enums;
using HcmIdentityProvisioning.Domain.ValueObjects;
using Xunit;

namespace HcmIdentityProvisioning.Domain.Tests.Entities;

public class EntityTests
{
    [Fact]
    public void Employee_ShouldHoldAttributesCorrectly()
    {
        var empId = EmployeeId.Create("EMP101").Value;
        var emp = new Employee(
            empId,
            "Carlos Silva",
            EmployeeStatus.Active,
            "Tecnologia",
            "Engenheiro de Software",
            new Dictionary<string, string> { ["email"] = "carlos@personal.com" }
        );

        emp.Id.Should().Be(empId);
        emp.FullName.Should().Be("Carlos Silva");
        emp.Status.Should().Be(EmployeeStatus.Active);
        emp.Department.Should().Be("Tecnologia");
        emp.JobTitle.Should().Be("Engenheiro de Software");
        emp.ExtendedAttributes["email"].Should().Be("carlos@personal.com");
    }

    [Fact]
    public void EntraUser_ShouldHoldAttributesCorrectly()
    {
        var graphId = Guid.NewGuid();
        var empId = EmployeeId.Create("EMP102").Value;
        var upn = UserPrincipalName.Create("carlos.silva@empresa.com").Value;
        var groupIds = new HashSet<Guid> { Guid.NewGuid(), Guid.NewGuid() };

        var entraUser = new EntraUser(
            graphId,
            empId,
            upn,
            "Carlos Silva",
            true,
            groupIds
        );

        entraUser.GraphId.Should().Be(graphId);
        entraUser.EmployeeId.Should().Be(empId);
        entraUser.UserPrincipalName.Should().Be(upn);
        entraUser.DisplayName.Should().Be("Carlos Silva");
        entraUser.AccountEnabled.Should().BeTrue();
        entraUser.AssignedGroupIds.Should().BeEquivalentTo(groupIds);
    }

    [Fact]
    public void ManagedGroup_ShouldHoldAttributesCorrectly()
    {
        var groupId = Guid.NewGuid();
        var group = new ManagedGroup(groupId, "Engenharia");

        group.Id.Should().Be(groupId);
        group.DisplayName.Should().Be("Engenharia");
    }

    [Fact]
    public void DeltaActionHierarchy_ShouldBePolymorphic()
    {
        var userId = Guid.NewGuid();
        var actions = new DeltaAction[]
        {
            new EnableAccountAction(userId),
            new RevokeSessionsAction(userId)
        };

        actions.Should().HaveCount(2);
        actions[0].TargetGraphId.Should().Be(userId);
    }

    [Fact]
    public void DeltaActionSubtypes_ShouldExposeExpectedPropertiesAndActionNames()
    {
        var userId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var empId = EmployeeId.Create("EMP103").Value;
        var emp = new Employee(empId, "Ana Costa", EmployeeStatus.Active, "RH", "Analista", new Dictionary<string, string>());
        var upn = UserPrincipalName.Create("ana.costa@empresa.com").Value;

        var createUser = new CreateUserAction(emp, upn, "TempPass123!");
        createUser.TargetGraphId.Should().BeNull();
        createUser.Employee.Should().Be(emp);
        createUser.UserPrincipalName.Should().Be(upn);
        createUser.TemporaryPassword.Should().Be("TempPass123!");
        createUser.ActionName.Should().Be("CreateUser");

        var updateName = new UpdateDisplayNameAction(userId, "Ana Costa Silva");
        updateName.TargetGraphId.Should().Be(userId);
        updateName.NewDisplayName.Should().Be("Ana Costa Silva");
        updateName.ActionName.Should().Be("UpdateDisplayName");

        var enable = new EnableAccountAction(userId);
        enable.TargetGraphId.Should().Be(userId);
        enable.ActionName.Should().Be("EnableAccount");

        var disable = new DisableAccountAction(userId);
        disable.TargetGraphId.Should().Be(userId);
        disable.ActionName.Should().Be("DisableAccount");

        var revoke = new RevokeSessionsAction(userId);
        revoke.TargetGraphId.Should().Be(userId);
        revoke.ActionName.Should().Be("RevokeSessions");

        var addGroup = new AddGroupMemberAction(userId, groupId, "RH-Geral");
        addGroup.TargetGraphId.Should().Be(userId);
        addGroup.GroupId.Should().Be(groupId);
        addGroup.GroupName.Should().Be("RH-Geral");
        addGroup.ActionName.Should().Be("AddGroupMember");

        var removeGroup = new RemoveGroupMemberAction(userId, groupId, "RH-Geral");
        removeGroup.TargetGraphId.Should().Be(userId);
        removeGroup.GroupId.Should().Be(groupId);
        removeGroup.GroupName.Should().Be("RH-Geral");
        removeGroup.ActionName.Should().Be("RemoveGroupMember");
    }
}
