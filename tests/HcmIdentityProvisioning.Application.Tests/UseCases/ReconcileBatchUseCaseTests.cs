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

public class ReconcileBatchUseCaseTests
{
    [Fact]
    public async Task ExecuteAsync_WhenBreakerTrips_HaltsDestructiveOperations()
    {
        var connector = Substitute.For<IHcmConnector>();
        var store = Substitute.For<IIdentityStore>();
        var rules = Substitute.For<IRulesEngine>();
        var pwdGen = Substitute.For<ISecurePasswordGenerator>();
        var breaker = Substitute.For<ICircuitBreaker>();
        var delivery = Substitute.For<ICredentialDeliveryService>();

        var emp1 = new Employee(EmployeeId.Create("E1").Value, "User 1", EmployeeStatus.Inactive, "Dep", "Role", new Dictionary<string, string>());
        var paged = new PagedResult<Employee>(new[] { emp1 }, 1, 50, 1, false);
        connector.GetEmployeesPageAsync(1, 50, Arg.Any<CancellationToken>()).Returns(paged);

        var existingUser = new EntraUser(Guid.NewGuid(), emp1.Id, UserPrincipalName.Create("user1@corp.com").Value, "User 1", true, new HashSet<Guid>());
        store.GetUsersByEmployeeIdsAsync(Arg.Any<IEnumerable<EmployeeId>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<EmployeeId, EntraUser> { [emp1.Id] = existingUser });

        breaker.ShouldTrip(Arg.Any<int>(), Arg.Any<IReadOnlyList<Domain.Actions.DeltaAction>>(), out Arg.Any<string>())
            .Returns(x => { x[2] = "Tripped by test"; return true; });

        var reconciler = new IdentityReconciliationService(rules, pwdGen);
        var settings = new SyncSettings { TenantDomain = "corp.com", HaltAllOperationsOnTrip = false };

        var useCase = new ReconcileBatchUseCase(
            connector,
            store,
            reconciler,
            breaker,
            delivery,
            settings,
            NullLogger<ReconcileBatchUseCase>.Instance
        );

        var report = await useCase.ExecuteAsync();

        report.CircuitBreakerTripped.Should().BeTrue();
        report.DisabledCount.Should().Be(0);
        report.SessionsRevokedCount.Should().Be(0);
        await store.DidNotReceive().ApplyBatchMutationsAsync(Arg.Any<IEnumerable<Domain.Actions.DeltaAction>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenBreakerTrips_AndHaltAllOperationsOnTripIsTrue_HaltsEntireBatchImmediately()
    {
        var connector = Substitute.For<IHcmConnector>();
        var store = Substitute.For<IIdentityStore>();
        var rules = Substitute.For<IRulesEngine>();
        var pwdGen = Substitute.For<ISecurePasswordGenerator>();
        var breaker = Substitute.For<ICircuitBreaker>();
        var delivery = Substitute.For<ICredentialDeliveryService>();

        var emp1 = new Employee(EmployeeId.Create("E1").Value, "User 1", EmployeeStatus.Inactive, "Dep", "Role", new Dictionary<string, string>());
        var paged1 = new PagedResult<Employee>(new[] { emp1 }, 1, 50, 2, true);
        connector.GetEmployeesPageAsync(1, 50, Arg.Any<CancellationToken>()).Returns(paged1);

        var existingUser = new EntraUser(Guid.NewGuid(), emp1.Id, UserPrincipalName.Create("user1@corp.com").Value, "User 1", true, new HashSet<Guid>());
        store.GetUsersByEmployeeIdsAsync(Arg.Any<IEnumerable<EmployeeId>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<EmployeeId, EntraUser> { [emp1.Id] = existingUser });

        breaker.ShouldTrip(Arg.Any<int>(), Arg.Any<IReadOnlyList<Domain.Actions.DeltaAction>>(), out Arg.Any<string>())
            .Returns(x => { x[2] = "Halt trip"; return true; });

        var reconciler = new IdentityReconciliationService(rules, pwdGen);
        var settings = new SyncSettings { TenantDomain = "corp.com", HaltAllOperationsOnTrip = true };

        var useCase = new ReconcileBatchUseCase(
            connector,
            store,
            reconciler,
            breaker,
            delivery,
            settings,
            NullLogger<ReconcileBatchUseCase>.Instance
        );

        var report = await useCase.ExecuteAsync();

        report.CircuitBreakerTripped.Should().BeTrue();
        report.CircuitBreakerMessage.Should().Be("Halt trip");
        await connector.DidNotReceive().GetEmployeesPageAsync(2, Arg.Any<int>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().ApplyBatchMutationsAsync(Arg.Any<IEnumerable<Domain.Actions.DeltaAction>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_SuccessfulBatch_CreatesUsersAndDeliversCredentials()
    {
        var connector = Substitute.For<IHcmConnector>();
        var store = Substitute.For<IIdentityStore>();
        var rules = Substitute.For<IRulesEngine>();
        var pwdGen = Substitute.For<ISecurePasswordGenerator>();
        var breaker = Substitute.For<ICircuitBreaker>();
        var delivery = Substitute.For<ICredentialDeliveryService>();

        pwdGen.GeneratePassword(Arg.Any<int>()).Returns("TempPassword123!");
        rules.EvaluateDesiredGroupsAsync(Arg.Any<Employee>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>());
        store.IsUserPrincipalNameAvailableAsync(Arg.Any<UserPrincipalName>(), Arg.Any<CancellationToken>())
            .Returns(true);
        store.GetManagedGroupsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ManagedGroup>());

        var emp1 = new Employee(EmployeeId.Create("E1").Value, "Alice Smith", EmployeeStatus.Active, "Engineering", "Developer", new Dictionary<string, string>());
        var paged = new PagedResult<Employee>(new[] { emp1 }, 1, 50, 1, false);
        connector.GetEmployeesPageAsync(1, 50, Arg.Any<CancellationToken>()).Returns(paged);

        store.GetUsersByEmployeeIdsAsync(Arg.Any<IEnumerable<EmployeeId>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<EmployeeId, EntraUser>());

        breaker.ShouldTrip(Arg.Any<int>(), Arg.Any<IReadOnlyList<Domain.Actions.DeltaAction>>(), out Arg.Any<string>())
            .Returns(false);

        var reconciler = new IdentityReconciliationService(rules, pwdGen);
        var settings = new SyncSettings { TenantDomain = "corp.com", HaltAllOperationsOnTrip = false };

        var useCase = new ReconcileBatchUseCase(
            connector,
            store,
            reconciler,
            breaker,
            delivery,
            settings,
            NullLogger<ReconcileBatchUseCase>.Instance
        );

        var report = await useCase.ExecuteAsync();

        report.CircuitBreakerTripped.Should().BeFalse();
        report.TotalProcessed.Should().Be(1);
        report.CreatedCount.Should().Be(1);

        await store.Received(1).ApplyBatchMutationsAsync(
            Arg.Is<IEnumerable<Domain.Actions.DeltaAction>>(actions => actions.Count() == 1),
            Arg.Any<CancellationToken>()
        );

        await delivery.Received(1).DeliverInitialCredentialsAsync(
            emp1,
            Arg.Is<UserPrincipalName>(u => u.Value == "alice.smith@corp.com"),
            "TempPassword123!",
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task ExecuteAsync_MultiplePages_PaginatesUntilDone()
    {
        var connector = Substitute.For<IHcmConnector>();
        var store = Substitute.For<IIdentityStore>();
        var rules = Substitute.For<IRulesEngine>();
        var pwdGen = Substitute.For<ISecurePasswordGenerator>();
        var breaker = Substitute.For<ICircuitBreaker>();
        var delivery = Substitute.For<ICredentialDeliveryService>();

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

        var useCase = new ReconcileBatchUseCase(
            connector,
            store,
            reconciler,
            breaker,
            delivery,
            settings,
            NullLogger<ReconcileBatchUseCase>.Instance
        );

        var report = await useCase.ExecuteAsync();

        report.TotalProcessed.Should().Be(2);
        report.CircuitBreakerTripped.Should().BeFalse();
        await connector.Received(1).GetEmployeesPageAsync(1, 50, Arg.Any<CancellationToken>());
        await connector.Received(1).GetEmployeesPageAsync(2, 50, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenBreakerTripsWithMixedActions_AppliesOnlyNonDestructiveActions()
    {
        var connector = Substitute.For<IHcmConnector>();
        var store = Substitute.For<IIdentityStore>();
        var rules = Substitute.For<IRulesEngine>();
        var pwdGen = Substitute.For<ISecurePasswordGenerator>();
        var breaker = Substitute.For<ICircuitBreaker>();
        var delivery = Substitute.For<ICredentialDeliveryService>();

        var empInactive = new Employee(EmployeeId.Create("E1").Value, "User 1", EmployeeStatus.Inactive, "Dep", "Role", new Dictionary<string, string>());
        var empActive = new Employee(EmployeeId.Create("E2").Value, "User Two Renamed", EmployeeStatus.Active, "Dep", "Role", new Dictionary<string, string>());

        var existing1 = new EntraUser(Guid.NewGuid(), empInactive.Id, UserPrincipalName.Create("user1@corp.com").Value, "User 1", true, new HashSet<Guid>());
        var existing2 = new EntraUser(Guid.NewGuid(), empActive.Id, UserPrincipalName.Create("user2@corp.com").Value, "User 2", true, new HashSet<Guid>());

        var paged = new PagedResult<Employee>(new[] { empInactive, empActive }, 1, 50, 2, false);
        connector.GetEmployeesPageAsync(1, 50, Arg.Any<CancellationToken>()).Returns(paged);

        store.GetUsersByEmployeeIdsAsync(Arg.Any<IEnumerable<EmployeeId>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<EmployeeId, EntraUser> { [empInactive.Id] = existing1, [empActive.Id] = existing2 });

        store.GetManagedGroupsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ManagedGroup>());
        rules.EvaluateDesiredGroupsAsync(Arg.Any<Employee>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>());

        breaker.ShouldTrip(Arg.Any<int>(), Arg.Any<IReadOnlyList<Domain.Actions.DeltaAction>>(), out Arg.Any<string>())
            .Returns(x => { x[2] = "Tripped"; return true; });

        var reconciler = new IdentityReconciliationService(rules, pwdGen);
        var settings = new SyncSettings { TenantDomain = "corp.com", HaltAllOperationsOnTrip = false };

        var useCase = new ReconcileBatchUseCase(
            connector,
            store,
            reconciler,
            breaker,
            delivery,
            settings,
            NullLogger<ReconcileBatchUseCase>.Instance
        );

        var report = await useCase.ExecuteAsync();

        report.CircuitBreakerTripped.Should().BeTrue();
        report.DisabledCount.Should().Be(0);
        report.UpdatedCount.Should().Be(1);

        await store.Received(1).ApplyBatchMutationsAsync(
            Arg.Is<IEnumerable<Domain.Actions.DeltaAction>>(actions =>
                actions.All(a => !(a is Domain.Actions.DisableAccountAction) && !(a is Domain.Actions.RevokeSessionsAction)) &&
                actions.Any(a => a is Domain.Actions.UpdateDisplayNameAction)),
            Arg.Any<CancellationToken>()
        );
    }
}
