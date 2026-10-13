using FluentAssertions;
using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Application.Services;
using HcmIdentityProvisioning.Application.UseCases;
using HcmIdentityProvisioning.Domain.Common;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Enums;
using HcmIdentityProvisioning.Domain.Policies;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace HcmIdentityProvisioning.Application.Tests.UseCases;

public class DryRunAuditUseCaseTests
{
    [Fact]
    public async Task ExecuteAsync_CalculatesMetricsWithoutMutatingStore()
    {
        var connector = Substitute.For<IHcmConnector>();
        var store = Substitute.For<IIdentityStore>();
        var rules = Substitute.For<IRulesEngine>();
        var pwdGen = Substitute.For<ISecurePasswordGenerator>();
        var breaker = Substitute.For<ICircuitBreaker>();

        pwdGen.GeneratePassword(Arg.Any<int>()).Returns("TempPwd123!");
        rules.EvaluateDesiredGroupsAsync(Arg.Any<Employee>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>());
        store.IsUserPrincipalNameAvailableAsync(Arg.Any<UserPrincipalName>(), Arg.Any<CancellationToken>())
            .Returns(true);
        store.GetManagedGroupsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ManagedGroup>());

        var emp1 = new Employee(EmployeeId.Create("E1").Value, "Alice Smith", EmployeeStatus.Active, "Engineering", "Developer", new Dictionary<string, string>());
        var emp2 = new Employee(EmployeeId.Create("E2").Value, "Bob Jones", EmployeeStatus.Inactive, "Sales", "Rep", new Dictionary<string, string>());
        var paged = new PagedResult<Employee>(new[] { emp1, emp2 }, 1, 50, 2, false);
        connector.GetEmployeesPageAsync(1, 50, Arg.Any<CancellationToken>()).Returns(paged);

        var existingBob = new EntraUser(Guid.NewGuid(), emp2.Id, UserPrincipalName.Create("bob.jones@corp.com").Value, "Bob Jones", true, new HashSet<Guid>());
        store.GetUsersByEmployeeIdsAsync(Arg.Any<IEnumerable<EmployeeId>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<EmployeeId, EntraUser> { [emp2.Id] = existingBob });

        breaker.ShouldTrip(Arg.Any<int>(), Arg.Any<IReadOnlyList<Domain.Actions.DeltaAction>>(), out Arg.Any<string>())
            .Returns(false);

        var reconciler = new IdentityReconciliationService(rules, pwdGen);
        var settings = new SyncSettings { TenantDomain = "corp.com" };

        var useCase = new DryRunAuditUseCase(
            connector,
            store,
            reconciler,
            breaker,
            settings,
            NullLogger<DryRunAuditUseCase>.Instance
        );

        var report = await useCase.ExecuteAsync();

        report.TotalProcessed.Should().Be(2);
        report.CreatedCount.Should().Be(1);
        report.DisabledCount.Should().Be(1);
        report.SessionsRevokedCount.Should().Be(1);
        report.CircuitBreakerTripped.Should().BeFalse();

        await store.DidNotReceive().ApplyBatchMutationsAsync(
            Arg.Any<IEnumerable<Domain.Actions.DeltaAction>>(),
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task ExecuteAsync_WhenBreakerTrips_RecordsTrippedStatusAndRetainsCounts()
    {
        var connector = Substitute.For<IHcmConnector>();
        var store = Substitute.For<IIdentityStore>();
        var rules = Substitute.For<IRulesEngine>();
        var pwdGen = Substitute.For<ISecurePasswordGenerator>();
        var breaker = Substitute.For<ICircuitBreaker>();

        var emp1 = new Employee(EmployeeId.Create("E1").Value, "User 1", EmployeeStatus.Inactive, "Dep", "Role", new Dictionary<string, string>());
        var paged = new PagedResult<Employee>(new[] { emp1 }, 1, 50, 1, false);
        connector.GetEmployeesPageAsync(1, 50, Arg.Any<CancellationToken>()).Returns(paged);

        var existingUser = new EntraUser(Guid.NewGuid(), emp1.Id, UserPrincipalName.Create("user1@corp.com").Value, "User 1", true, new HashSet<Guid>());
        store.GetUsersByEmployeeIdsAsync(Arg.Any<IEnumerable<EmployeeId>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<EmployeeId, EntraUser> { [emp1.Id] = existingUser });

        store.GetManagedGroupsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ManagedGroup>());

        breaker.ShouldTrip(Arg.Any<int>(), Arg.Any<IReadOnlyList<Domain.Actions.DeltaAction>>(), out Arg.Any<string>())
            .Returns(x => { x[2] = "High disablement rate"; return true; });

        var reconciler = new IdentityReconciliationService(rules, pwdGen);
        var settings = new SyncSettings { TenantDomain = "corp.com" };

        var useCase = new DryRunAuditUseCase(
            connector,
            store,
            reconciler,
            breaker,
            settings,
            NullLogger<DryRunAuditUseCase>.Instance
        );

        var report = await useCase.ExecuteAsync();

        report.CircuitBreakerTripped.Should().BeTrue();
        report.CircuitBreakerMessage.Should().Be("High disablement rate");
        report.DisabledCount.Should().Be(1);
        report.SessionsRevokedCount.Should().Be(1);

        await store.DidNotReceive().ApplyBatchMutationsAsync(
            Arg.Any<IEnumerable<Domain.Actions.DeltaAction>>(),
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task ExecuteAsync_MultiplePages_PaginatesAllPages()
    {
        var connector = Substitute.For<IHcmConnector>();
        var store = Substitute.For<IIdentityStore>();
        var rules = Substitute.For<IRulesEngine>();
        var pwdGen = Substitute.For<ISecurePasswordGenerator>();
        var breaker = Substitute.For<ICircuitBreaker>();

        rules.EvaluateDesiredGroupsAsync(Arg.Any<Employee>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>());
        store.GetManagedGroupsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ManagedGroup>());

        var emp1 = new Employee(EmployeeId.Create("E1").Value, "User 1", EmployeeStatus.Active, "IT", "Dev", new Dictionary<string, string>());
        var emp2 = new Employee(EmployeeId.Create("E2").Value, "User 2", EmployeeStatus.Active, "IT", "Dev", new Dictionary<string, string>());

        var user1 = new EntraUser(Guid.NewGuid(), emp1.Id, UserPrincipalName.Create("user1@corp.com").Value, "User 1", true, new HashSet<Guid>());
        var user2 = new EntraUser(Guid.NewGuid(), emp2.Id, UserPrincipalName.Create("user2@corp.com").Value, "User 2", true, new HashSet<Guid>());

        var paged1 = new PagedResult<Employee>(new[] { emp1 }, 1, 50, 2, true);
        var paged2 = new PagedResult<Employee>(new[] { emp2 }, 2, 50, 2, false);

        connector.GetEmployeesPageAsync(1, 50, Arg.Any<CancellationToken>()).Returns(paged1);
        connector.GetEmployeesPageAsync(2, 50, Arg.Any<CancellationToken>()).Returns(paged2);

        store.GetUsersByEmployeeIdsAsync(Arg.Is<IEnumerable<EmployeeId>>(ids => ids.Contains(emp1.Id)), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<EmployeeId, EntraUser> { [emp1.Id] = user1 });
        store.GetUsersByEmployeeIdsAsync(Arg.Is<IEnumerable<EmployeeId>>(ids => ids.Contains(emp2.Id)), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<EmployeeId, EntraUser> { [emp2.Id] = user2 });

        breaker.ShouldTrip(Arg.Any<int>(), Arg.Any<IReadOnlyList<Domain.Actions.DeltaAction>>(), out Arg.Any<string>())
            .Returns(false);

        var reconciler = new IdentityReconciliationService(rules, pwdGen);
        var settings = new SyncSettings { TenantDomain = "corp.com" };

        var useCase = new DryRunAuditUseCase(
            connector,
            store,
            reconciler,
            breaker,
            settings,
            NullLogger<DryRunAuditUseCase>.Instance
        );

        var report = await useCase.ExecuteAsync();

        report.TotalProcessed.Should().Be(2);
        await connector.Received(1).GetEmployeesPageAsync(1, 50, Arg.Any<CancellationToken>());
        await connector.Received(1).GetEmployeesPageAsync(2, 50, Arg.Any<CancellationToken>());
        await store.DidNotReceive().ApplyBatchMutationsAsync(Arg.Any<IEnumerable<Domain.Actions.DeltaAction>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenPage1Trips_RetainsTrippedStateAcrossSubsequentPages()
    {
        var connector = Substitute.For<IHcmConnector>();
        var store = Substitute.For<IIdentityStore>();
        var rules = Substitute.For<IRulesEngine>();
        var pwdGen = Substitute.For<ISecurePasswordGenerator>();
        var breaker = Substitute.For<ICircuitBreaker>();

        rules.EvaluateDesiredGroupsAsync(Arg.Any<Employee>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>());
        store.GetManagedGroupsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ManagedGroup>());

        var emp1 = new Employee(EmployeeId.Create("E1").Value, "User 1", EmployeeStatus.Inactive, "IT", "Dev", new Dictionary<string, string>());
        var emp2 = new Employee(EmployeeId.Create("E2").Value, "User 2", EmployeeStatus.Inactive, "IT", "Dev", new Dictionary<string, string>());

        var user1 = new EntraUser(Guid.NewGuid(), emp1.Id, UserPrincipalName.Create("user1@corp.com").Value, "User 1", true, new HashSet<Guid>());
        var user2 = new EntraUser(Guid.NewGuid(), emp2.Id, UserPrincipalName.Create("user2@corp.com").Value, "User 2", true, new HashSet<Guid>());

        var paged1 = new PagedResult<Employee>(new[] { emp1 }, 1, 50, 2, true);
        var paged2 = new PagedResult<Employee>(new[] { emp2 }, 2, 50, 2, false);

        connector.GetEmployeesPageAsync(1, 50, Arg.Any<CancellationToken>()).Returns(paged1);
        connector.GetEmployeesPageAsync(2, 50, Arg.Any<CancellationToken>()).Returns(paged2);

        store.GetUsersByEmployeeIdsAsync(Arg.Is<IEnumerable<EmployeeId>>(ids => ids.Contains(emp1.Id)), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<EmployeeId, EntraUser> { [emp1.Id] = user1 });
        store.GetUsersByEmployeeIdsAsync(Arg.Is<IEnumerable<EmployeeId>>(ids => ids.Contains(emp2.Id)), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<EmployeeId, EntraUser> { [emp2.Id] = user2 });

        breaker.ShouldTrip(1, Arg.Any<IReadOnlyList<Domain.Actions.DeltaAction>>(), out Arg.Any<string>())
            .Returns(x => { x[2] = "Page 1 tripped"; return true; });
        breaker.ShouldTrip(2, Arg.Any<IReadOnlyList<Domain.Actions.DeltaAction>>(), out Arg.Any<string>())
            .Returns(false);

        var reconciler = new IdentityReconciliationService(rules, pwdGen);
        var settings = new SyncSettings { TenantDomain = "corp.com" };

        var useCase = new DryRunAuditUseCase(
            connector,
            store,
            reconciler,
            breaker,
            settings,
            NullLogger<DryRunAuditUseCase>.Instance
        );

        var report = await useCase.ExecuteAsync();

        report.CircuitBreakerTripped.Should().BeTrue();
        report.CircuitBreakerMessage.Should().Be("Page 1 tripped");
        report.TotalProcessed.Should().Be(2);
        report.DisabledCount.Should().Be(2, "dry run audits all proposed actions across pages");
    }

    [Fact]
    public async Task ExecuteAsync_WhenConnectorRepeatsEmployeesInSubsequentPage_HaltsPaginationToPreventCycle()
    {
        var connector = Substitute.For<IHcmConnector>();
        var store = Substitute.For<IIdentityStore>();
        var rules = Substitute.For<IRulesEngine>();
        var pwdGen = Substitute.For<ISecurePasswordGenerator>();
        var breaker = Substitute.For<ICircuitBreaker>();

        pwdGen.GeneratePassword(Arg.Any<int>()).Returns("TempPwd123!");
        rules.EvaluateDesiredGroupsAsync(Arg.Any<Employee>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>());
        store.IsUserPrincipalNameAvailableAsync(Arg.Any<UserPrincipalName>(), Arg.Any<CancellationToken>())
            .Returns(true);
        store.GetManagedGroupsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ManagedGroup>());

        var emp1 = new Employee(EmployeeId.Create("E1").Value, "Alice Smith", EmployeeStatus.Active, "Engineering", "Developer", new Dictionary<string, string>());

        var paged1 = new PagedResult<Employee>(new[] { emp1 }, 1, 50, 1, true);
        var paged2 = new PagedResult<Employee>(new[] { emp1 }, 2, 50, 1, true);
        var paged3 = new PagedResult<Employee>(new[] { emp1 }, 3, 50, 1, false);

        connector.GetEmployeesPageAsync(1, 50, Arg.Any<CancellationToken>()).Returns(paged1);
        connector.GetEmployeesPageAsync(2, 50, Arg.Any<CancellationToken>()).Returns(paged2);
        connector.GetEmployeesPageAsync(3, 50, Arg.Any<CancellationToken>()).Returns(paged3);

        store.GetUsersByEmployeeIdsAsync(Arg.Any<IEnumerable<EmployeeId>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<EmployeeId, EntraUser>());

        breaker.ShouldTrip(Arg.Any<int>(), Arg.Any<IReadOnlyList<Domain.Actions.DeltaAction>>(), out Arg.Any<string>())
            .Returns(false);

        var reconciler = new IdentityReconciliationService(rules, pwdGen);
        var settings = new SyncSettings { TenantDomain = "corp.com", BatchPageSize = 50 };

        var useCase = new DryRunAuditUseCase(
            connector,
            store,
            reconciler,
            breaker,
            settings,
            NullLogger<DryRunAuditUseCase>.Instance
        );

        var report = await useCase.ExecuteAsync();

        report.TotalProcessed.Should().Be(2);
        report.CreatedCount.Should().Be(1);
        await connector.Received(1).GetEmployeesPageAsync(1, 50, Arg.Any<CancellationToken>());
        await connector.Received(1).GetEmployeesPageAsync(2, 50, Arg.Any<CancellationToken>());
        await connector.DidNotReceive().GetEmployeesPageAsync(3, 50, Arg.Any<CancellationToken>());
    }
}
