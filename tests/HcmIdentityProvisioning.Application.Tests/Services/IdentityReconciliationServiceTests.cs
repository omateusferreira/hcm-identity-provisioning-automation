using FluentAssertions;
using HcmIdentityProvisioning.Application.Models;
using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Application.Services;
using HcmIdentityProvisioning.Domain.Actions;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Enums;
using HcmIdentityProvisioning.Domain.Policies;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Domain.ValueObjects;
using NSubstitute;
using Xunit;

namespace HcmIdentityProvisioning.Application.Tests.Services;

public class IdentityReconciliationServiceTests
{
    private readonly IRulesEngine _rulesEngine = Substitute.For<IRulesEngine>();
    private readonly ISecurePasswordGenerator _pwdGen = Substitute.For<ISecurePasswordGenerator>();

    public IdentityReconciliationServiceTests()
    {
        _pwdGen.GeneratePassword(Arg.Any<int>()).Returns("MockPassword123!");
    }

    [Fact]
    public async Task Reconcile_WhenJoiner_ProducesCreateActionAndGroupAssignments()
    {
        var service = new IdentityReconciliationService(_rulesEngine, _pwdGen);
        var emp = new Employee(
            EmployeeId.Create("101").Value,
            "Carlos Silva",
            EmployeeStatus.Active,
            "Tecnologia",
            "Engenheiro",
            new Dictionary<string, string>()
        );

        _rulesEngine.EvaluateDesiredGroupsAsync(emp).Returns(new HashSet<string> { "grp-iam-engineering" });

        var groupGuid = Guid.NewGuid();
        var managedGroups = new Dictionary<string, ManagedGroup>(StringComparer.OrdinalIgnoreCase)
        {
            ["grp-iam-engineering"] = new(groupGuid, "grp-iam-engineering")
        };

        var actions = await service.ReconcileEmployeeAsync(
            emp,
            existingUser: null,
            tenantDomain: "corp.com",
            managedGroups: managedGroups,
            isUpnAvailable: _ => Task.FromResult(true)
        );

        actions.Should().ContainSingle(a => a is CreateUserAction);
        var createAction = actions.OfType<CreateUserAction>().Single();
        createAction.Employee.Should().Be(emp);
        createAction.UserPrincipalName.Value.Should().Be("carlos.silva@corp.com");
        createAction.TemporaryPassword.Should().Be("MockPassword123!");
        createAction.TargetGraphId.Should().BeNull();

        actions.Should().ContainSingle(a => a is AddGroupMemberAction);
        var addGroup = actions.OfType<AddGroupMemberAction>().Single();
        addGroup.GraphId.Should().Be(Guid.Empty);
        addGroup.GroupId.Should().Be(groupGuid);
        addGroup.GroupName.Should().Be("grp-iam-engineering");
    }

    [Fact]
    public async Task Reconcile_WhenJoinerWithDiacriticsAndSpaces_SanitizesUpnCorrectly()
    {
        var service = new IdentityReconciliationService(_rulesEngine, _pwdGen);
        var emp = new Employee(
            EmployeeId.Create("102").Value,
            "João Victor Müller da Silva",
            EmployeeStatus.Active,
            "Financeiro",
            "Analista",
            new Dictionary<string, string>()
        );

        _rulesEngine.EvaluateDesiredGroupsAsync(emp).Returns(new HashSet<string>());

        var actions = await service.ReconcileEmployeeAsync(
            emp,
            existingUser: null,
            tenantDomain: "corp.com",
            managedGroups: new Dictionary<string, ManagedGroup>(),
            isUpnAvailable: _ => Task.FromResult(true)
        );

        var createAction = actions.OfType<CreateUserAction>().Single();
        createAction.UserPrincipalName.Value.Should().Be("joao.silva@corp.com");
    }

    [Fact]
    public async Task Reconcile_WhenJoinerAndUpnCollides_ResolvesNextAvailableNumberedUpn()
    {
        var service = new IdentityReconciliationService(_rulesEngine, _pwdGen);
        var emp = new Employee(
            EmployeeId.Create("103").Value,
            "Carlos Silva",
            EmployeeStatus.Active,
            "Tecnologia",
            "Engenheiro",
            new Dictionary<string, string>()
        );

        _rulesEngine.EvaluateDesiredGroupsAsync(emp).Returns(new HashSet<string>());

        var availableUpns = new HashSet<string> { "carlos.silva2@corp.com" };

        var actions = await service.ReconcileEmployeeAsync(
            emp,
            existingUser: null,
            tenantDomain: "corp.com",
            managedGroups: new Dictionary<string, ManagedGroup>(),
            isUpnAvailable: upn => Task.FromResult(availableUpns.Contains(upn.Value))
        );

        var createAction = actions.OfType<CreateUserAction>().Single();
        createAction.UserPrincipalName.Value.Should().Be("carlos.silva2@corp.com");
    }

    [Fact]
    public async Task Reconcile_WhenJoinerAndUpnCollisionExceedsLimit_ThrowsInvalidOperationException()
    {
        var service = new IdentityReconciliationService(_rulesEngine, _pwdGen);
        var emp = new Employee(
            EmployeeId.Create("104").Value,
            "Carlos Silva",
            EmployeeStatus.Active,
            "Tecnologia",
            "Engenheiro",
            new Dictionary<string, string>()
        );

        _rulesEngine.EvaluateDesiredGroupsAsync(emp).Returns(new HashSet<string>());

        var act = async () => await service.ReconcileEmployeeAsync(
            emp,
            existingUser: null,
            tenantDomain: "corp.com",
            managedGroups: new Dictionary<string, ManagedGroup>(),
            isUpnAvailable: _ => Task.FromResult(false)
        );

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Could not allocate available UPN*");
    }

    [Fact]
    public async Task Reconcile_WhenJoinerAndEmployeeIsInactive_ReturnsEmptyActions()
    {
        var service = new IdentityReconciliationService(_rulesEngine, _pwdGen);
        var emp = new Employee(
            EmployeeId.Create("105").Value,
            "Carlos Inativo",
            EmployeeStatus.Inactive,
            "Tecnologia",
            "Engenheiro",
            new Dictionary<string, string>()
        );

        _rulesEngine.EvaluateDesiredGroupsAsync(emp).Returns(new HashSet<string> { "grp-iam-engineering" });

        var actions = await service.ReconcileEmployeeAsync(
            emp,
            existingUser: null,
            tenantDomain: "corp.com",
            managedGroups: new Dictionary<string, ManagedGroup>(),
            isUpnAvailable: _ => Task.FromResult(true)
        );

        actions.Should().BeEmpty();
    }

    [Fact]
    public async Task Reconcile_WhenLeaverAndAccountActive_DisablesAccountRevokesSessionsAndRemovesManagedGroups()
    {
        var service = new IdentityReconciliationService(_rulesEngine, _pwdGen);
        var emp = new Employee(
            EmployeeId.Create("201").Value,
            "Ana Souza",
            EmployeeStatus.Inactive,
            "Operações",
            "Coordenadora",
            new Dictionary<string, string>()
        );

        _rulesEngine.EvaluateDesiredGroupsAsync(emp).Returns(new HashSet<string>());

        var userGuid = Guid.NewGuid();
        var managedGroupGuid1 = Guid.NewGuid();
        var managedGroupGuid2 = Guid.NewGuid();
        var unmanagedGroupGuid = Guid.NewGuid();

        var existingUser = new EntraUser(
            userGuid,
            emp.Id,
            UserPrincipalName.Create("ana.souza@corp.com").Value,
            "Ana Souza",
            AccountEnabled: true,
            AssignedGroupIds: new HashSet<Guid> { managedGroupGuid1, managedGroupGuid2, unmanagedGroupGuid }
        );

        var managedGroups = new Dictionary<string, ManagedGroup>(StringComparer.OrdinalIgnoreCase)
        {
            ["grp-iam-ops"] = new(managedGroupGuid1, "grp-iam-ops"),
            ["grp-iam-coordinators"] = new(managedGroupGuid2, "grp-iam-coordinators")
        };

        var actions = await service.ReconcileEmployeeAsync(
            emp,
            existingUser: existingUser,
            tenantDomain: "corp.com",
            managedGroups: managedGroups,
            isUpnAvailable: _ => Task.FromResult(true)
        );

        actions.Should().ContainSingle(a => a is DisableAccountAction && a.TargetGraphId == userGuid);
        actions.Should().ContainSingle(a => a is RevokeSessionsAction && a.TargetGraphId == userGuid);

        var removeActions = actions.OfType<RemoveGroupMemberAction>().ToList();
        removeActions.Should().HaveCount(2);
        removeActions.Select(r => r.GroupId).Should().BeEquivalentTo(new[] { managedGroupGuid1, managedGroupGuid2 });
        // Verify unmanaged group is untouched
        removeActions.Should().NotContain(r => r.GroupId == unmanagedGroupGuid);
    }

    [Fact]
    public async Task Reconcile_WhenLeaverAndAccountAlreadyDisabled_DoesNotDuplicateDisableAndRevoke()
    {
        var service = new IdentityReconciliationService(_rulesEngine, _pwdGen);
        var emp = new Employee(
            EmployeeId.Create("202").Value,
            "Lucas Ramos",
            EmployeeStatus.Inactive,
            "Operações",
            "Analista",
            new Dictionary<string, string>()
        );

        _rulesEngine.EvaluateDesiredGroupsAsync(emp).Returns(new HashSet<string>());

        var userGuid = Guid.NewGuid();
        var managedGroupGuid = Guid.NewGuid();

        var existingUser = new EntraUser(
            userGuid,
            emp.Id,
            UserPrincipalName.Create("lucas.ramos@corp.com").Value,
            "Lucas Ramos",
            AccountEnabled: false,
            AssignedGroupIds: new HashSet<Guid> { managedGroupGuid }
        );

        var managedGroups = new Dictionary<string, ManagedGroup>(StringComparer.OrdinalIgnoreCase)
        {
            ["grp-iam-ops"] = new(managedGroupGuid, "grp-iam-ops")
        };

        var actions = await service.ReconcileEmployeeAsync(
            emp,
            existingUser: existingUser,
            tenantDomain: "corp.com",
            managedGroups: managedGroups,
            isUpnAvailable: _ => Task.FromResult(true)
        );

        actions.Should().NotContain(a => a is DisableAccountAction);
        actions.Should().NotContain(a => a is RevokeSessionsAction);
        actions.Should().ContainSingle(a => a is RemoveGroupMemberAction);
    }

    [Fact]
    public async Task Reconcile_WhenMover_UpdatesDisplayNameAddsNewGroupsAndRemovesObsoleteManagedGroups()
    {
        var service = new IdentityReconciliationService(_rulesEngine, _pwdGen);
        var emp = new Employee(
            EmployeeId.Create("301").Value,
            "Mariana Ribeiro Silva", // Updated name
            EmployeeStatus.Active,
            "Engenharia",
            "Tech Lead",
            new Dictionary<string, string>()
        );

        // Desired: grp-iam-eng, grp-iam-leads
        _rulesEngine.EvaluateDesiredGroupsAsync(emp).Returns(new HashSet<string>
        {
            "grp-iam-eng",
            "grp-iam-leads"
        });

        var userGuid = Guid.NewGuid();
        var engGroupGuid = Guid.NewGuid();
        var leadsGroupGuid = Guid.NewGuid();
        var oldOpsGroupGuid = Guid.NewGuid();
        var unmanagedGroupGuid = Guid.NewGuid();

        var existingUser = new EntraUser(
            userGuid,
            emp.Id,
            UserPrincipalName.Create("mariana.ribeiro@corp.com").Value,
            "Mariana Ribeiro", // Old name
            AccountEnabled: false, // Was disabled
            AssignedGroupIds: new HashSet<Guid> { engGroupGuid, oldOpsGroupGuid, unmanagedGroupGuid }
        );

        var managedGroups = new Dictionary<string, ManagedGroup>(StringComparer.OrdinalIgnoreCase)
        {
            ["grp-iam-eng"] = new(engGroupGuid, "grp-iam-eng"),
            ["grp-iam-leads"] = new(leadsGroupGuid, "grp-iam-leads"),
            ["grp-iam-ops"] = new(oldOpsGroupGuid, "grp-iam-ops")
        };

        var actions = await service.ReconcileEmployeeAsync(
            emp,
            existingUser: existingUser,
            tenantDomain: "corp.com",
            managedGroups: managedGroups,
            isUpnAvailable: _ => Task.FromResult(true)
        );

        // 1. Account re-enabled
        actions.Should().ContainSingle(a => a is EnableAccountAction && a.TargetGraphId == userGuid);

        // 2. Display name updated
        var updateAction = actions.OfType<UpdateDisplayNameAction>().Single();
        updateAction.GraphId.Should().Be(userGuid);
        updateAction.NewDisplayName.Should().Be("Mariana Ribeiro Silva");

        // 3. New group added (leads), already assigned (eng) is not duplicated
        var addActions = actions.OfType<AddGroupMemberAction>().ToList();
        addActions.Should().ContainSingle();
        addActions.Single().GroupId.Should().Be(leadsGroupGuid);
        addActions.Single().GroupName.Should().Be("grp-iam-leads");

        // 4. Obsolete managed group removed (ops), unmanaged group preserved
        var removeActions = actions.OfType<RemoveGroupMemberAction>().ToList();
        removeActions.Should().ContainSingle();
        removeActions.Single().GroupId.Should().Be(oldOpsGroupGuid);
        removeActions.Single().GroupName.Should().Be("grp-iam-ops");
        actions.Should().NotContain(a => a is RemoveGroupMemberAction && ((RemoveGroupMemberAction)a).GroupId == unmanagedGroupGuid);
    }

    [Fact]
    public async Task Reconcile_WhenIdempotent_ProducesNoActions()
    {
        var service = new IdentityReconciliationService(_rulesEngine, _pwdGen);
        var emp = new Employee(
            EmployeeId.Create("401").Value,
            "Pedro Alvares",
            EmployeeStatus.Active,
            "TI",
            "Suporte",
            new Dictionary<string, string>()
        );

        _rulesEngine.EvaluateDesiredGroupsAsync(emp).Returns(new HashSet<string> { "grp-iam-support" });

        var userGuid = Guid.NewGuid();
        var supportGroupGuid = Guid.NewGuid();
        var unmanagedGroupGuid = Guid.NewGuid(); // Outside managedGroups, should not cause removals

        var existingUser = new EntraUser(
            userGuid,
            emp.Id,
            UserPrincipalName.Create("pedro.alvares@corp.com").Value,
            "Pedro Alvares",
            AccountEnabled: true,
            AssignedGroupIds: new HashSet<Guid> { supportGroupGuid, unmanagedGroupGuid }
        );

        var managedGroups = new Dictionary<string, ManagedGroup>(StringComparer.OrdinalIgnoreCase)
        {
            ["grp-iam-support"] = new(supportGroupGuid, "grp-iam-support")
        };

        var actions = await service.ReconcileEmployeeAsync(
            emp,
            existingUser: existingUser,
            tenantDomain: "corp.com",
            managedGroups: managedGroups,
            isUpnAvailable: _ => Task.FromResult(true)
        );

        actions.Should().BeEmpty();
    }

    [Fact]
    public async Task Reconcile_WhenDesiredManagedGroupNotFound_InvokesWarningCallbackAndDoesNotBreak()
    {
        var service = new IdentityReconciliationService(_rulesEngine, _pwdGen);
        var emp = new Employee(
            EmployeeId.Create("501").Value,
            "Camila Santos",
            EmployeeStatus.Active,
            "Marketing",
            "Designer",
            new Dictionary<string, string>()
        );

        _rulesEngine.EvaluateDesiredGroupsAsync(emp).Returns(new HashSet<string>
        {
            "grp-iam-valid",
            "grp-iam-missing"
        });

        var validGroupGuid = Guid.NewGuid();
        var managedGroups = new Dictionary<string, ManagedGroup>(StringComparer.OrdinalIgnoreCase)
        {
            ["grp-iam-valid"] = new(validGroupGuid, "grp-iam-valid")
        };

        var warnings = new List<string>();

        var actions = await service.ReconcileEmployeeAsync(
            emp,
            existingUser: null,
            tenantDomain: "corp.com",
            managedGroups: managedGroups,
            isUpnAvailable: _ => Task.FromResult(true),
            onWarning: w => warnings.Add(w)
        );

        // Warning callback invoked
        warnings.Should().ContainSingle(w => w.Contains("WARNING_MANAGED_GROUP_NOT_FOUND") && w.Contains("grp-iam-missing"));

        // Valid group still added
        actions.Should().ContainSingle(a => a is CreateUserAction);
        actions.Should().ContainSingle(a => a is AddGroupMemberAction && ((AddGroupMemberAction)a).GroupId == validGroupGuid);
    }

    [Fact]
    public async Task Reconcile_WhenMoverWithMissingGroup_InvokesWarningCallbackAndProcessesExisting()
    {
        var service = new IdentityReconciliationService(_rulesEngine, _pwdGen);
        var emp = new Employee(
            EmployeeId.Create("502").Value,
            "Camila Santos",
            EmployeeStatus.Active,
            "Marketing",
            "Designer",
            new Dictionary<string, string>()
        );

        _rulesEngine.EvaluateDesiredGroupsAsync(emp).Returns(new HashSet<string>
        {
            "grp-iam-nonexistent"
        });

        var userGuid = Guid.NewGuid();
        var existingUser = new EntraUser(
            userGuid,
            emp.Id,
            UserPrincipalName.Create("camila.santos@corp.com").Value,
            "Camila Santos",
            AccountEnabled: true,
            AssignedGroupIds: new HashSet<Guid>()
        );

        var warnings = new List<string>();

        var actions = await service.ReconcileEmployeeAsync(
            emp,
            existingUser: existingUser,
            tenantDomain: "corp.com",
            managedGroups: new Dictionary<string, ManagedGroup>(),
            isUpnAvailable: _ => Task.FromResult(true),
            onWarning: w => warnings.Add(w)
        );

        warnings.Should().ContainSingle(w => w.Contains("WARNING_MANAGED_GROUP_NOT_FOUND") && w.Contains("grp-iam-nonexistent"));
        actions.Should().BeEmpty();
    }

    [Fact]
    public void ChangeSet_EmptyAndNonEmpty_BehavesCorrectly()
    {
        ChangeSet.Empty.IsEmpty.Should().BeTrue();
        ChangeSet.Empty.Actions.Should().BeEmpty();

        var nonNullAction = new EnableAccountAction(Guid.NewGuid());
        var changeSet = new ChangeSet(new[] { nonNullAction });
        changeSet.IsEmpty.Should().BeFalse();
        changeSet.Actions.Should().ContainSingle();
    }

    [Fact]
    public void SyncSettings_DefaultValues_MatchSpecification()
    {
        var settings = new SyncSettings();

        settings.TenantDomain.Should().Be("company.onmicrosoft.com");
        settings.ManagedGroupPrefix.Should().Be("grp-iam-");
        settings.BatchPageSize.Should().Be(50);
        settings.MaxDisablementPercentage.Should().Be(10.0);
        settings.MaxDisablementCount.Should().Be(25);
        settings.HaltAllOperationsOnTrip.Should().BeFalse();
    }

    [Fact]
    public void SyncReport_RecordProperties_AreInitializedCorrectly()
    {
        var report = new SyncReport(
            TotalProcessed: 100,
            CreatedCount: 10,
            UpdatedCount: 5,
            EnabledCount: 2,
            DisabledCount: 3,
            SessionsRevokedCount: 3,
            GroupMembershipsAdded: 15,
            GroupMembershipsRemoved: 8,
            CircuitBreakerTripped: false,
            CircuitBreakerMessage: null,
            Warnings: new[] { "Warning 1" }
        );

        report.TotalProcessed.Should().Be(100);
        report.CreatedCount.Should().Be(10);
        report.UpdatedCount.Should().Be(5);
        report.EnabledCount.Should().Be(2);
        report.DisabledCount.Should().Be(3);
        report.SessionsRevokedCount.Should().Be(3);
        report.GroupMembershipsAdded.Should().Be(15);
        report.GroupMembershipsRemoved.Should().Be(8);
        report.CircuitBreakerTripped.Should().BeFalse();
        report.CircuitBreakerMessage.Should().BeNull();
        report.Warnings.Should().ContainSingle().Which.Should().Be("Warning 1");
    }
}
