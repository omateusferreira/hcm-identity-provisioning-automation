using System.CommandLine;
using FluentAssertions;
using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Application.Services;
using HcmIdentityProvisioning.Application.UseCases;
using HcmIdentityProvisioning.Admin.Commands;
using HcmIdentityProvisioning.Admin.Utils;
using HcmIdentityProvisioning.Domain.Actions;
using HcmIdentityProvisioning.Domain.Common;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Enums;
using HcmIdentityProvisioning.Domain.Policies;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace HcmIdentityProvisioning.Cli.Tests;

public class SyncCommandTests
{
    private static Employee CreateTestEmployee(string id = "EMP001", string name = "Ada Lovelace") =>
        new Employee(
            EmployeeId.Create(id).Value,
            name,
            EmployeeStatus.Active,
            "Engineering",
            "Developer",
            new Dictionary<string, string>()
        );

    private static (IServiceProvider sp, IIdentityStore store) CreateMockServiceProviderWithStore(
        bool tripCircuitBreaker = false,
        IReadOnlyList<Employee>? employees = null)
    {
        var services = new ServiceCollection();

        var connector = Substitute.For<IHcmConnector>();
        var employeeList = employees ?? [];
        connector.GetEmployeesPageAsync(1, 50, Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Employee>(employeeList, 1, 50, employeeList.Count, false));

        var store = Substitute.For<IIdentityStore>();
        store.GetManagedGroupsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, ManagedGroup>>(new Dictionary<string, ManagedGroup>
            {
                ["grp-iam-engineering"] = new ManagedGroup(Guid.NewGuid(), "grp-iam-engineering")
            }));
        store.GetUsersByEmployeeIdsAsync(Arg.Any<IEnumerable<EmployeeId>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<EmployeeId, EntraUser>>(new Dictionary<EmployeeId, EntraUser>()));
        store.IsUserPrincipalNameAvailableAsync(Arg.Any<UserPrincipalName>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var rulesEngine = Substitute.For<IRulesEngine>();
        rulesEngine.EvaluateDesiredGroupsAsync(Arg.Any<Employee>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<string>>(new HashSet<string> { "grp-iam-engineering" }));

        var pwdGen = Substitute.For<ISecurePasswordGenerator>();
        pwdGen.GeneratePassword(Arg.Any<int>()).Returns("TempPass123!");

        var reconciler = new IdentityReconciliationService(rulesEngine, pwdGen);

        var circuitBreaker = Substitute.For<ICircuitBreaker>();
        circuitBreaker.ShouldTrip(Arg.Any<int>(), Arg.Any<IReadOnlyList<DeltaAction>>(), out Arg.Any<string>())
            .Returns(x =>
            {
                x[2] = tripCircuitBreaker ? "Tripped by test" : null;
                return tripCircuitBreaker;
            });

        var credentialDelivery = Substitute.For<ICredentialDeliveryService>();

        var settings = new SyncSettings
        {
            TenantDomain = "company.onmicrosoft.com",
            ManagedGroupPrefix = "grp-iam-"
        };

        var reconcileUseCase = new ReconcileBatchUseCase(
            connector,
            store,
            reconciler,
            circuitBreaker,
            credentialDelivery,
            settings,
            NullLogger<ReconcileBatchUseCase>.Instance
        );

        var dryRunUseCase = new DryRunAuditUseCase(
            connector,
            store,
            reconciler,
            circuitBreaker,
            settings,
            NullLogger<DryRunAuditUseCase>.Instance
        );

        services.AddScoped(_ => reconcileUseCase);
        services.AddScoped(_ => dryRunUseCase);

        return (services.BuildServiceProvider(), store);
    }

    private static IServiceProvider CreateMockServiceProvider(bool tripCircuitBreaker = false) =>
        CreateMockServiceProviderWithStore(tripCircuitBreaker).sp;

    [Fact]
    public async Task InvokeAsync_WithDefaultOptions_PassesInMemoryDefaultsToFactory()
    {
        // Arrange
        SyncCliOptions? capturedOptions = null;
        var sp = CreateMockServiceProvider();

        var cmd = SyncCommand.Create(opts =>
        {
            capturedOptions = opts;
            return sp;
        });

        // Act
        var exitCode = await cmd.InvokeAsync(["--rules", "rules.json", "--fixtures", "fixtures.json"]);

        // Assert
        exitCode.Should().Be(0);
        capturedOptions.Should().NotBeNull();
        capturedOptions!.Idp.Should().Be("in-memory");
        capturedOptions.TenantDomain.Should().Be("company.onmicrosoft.com");
        capturedOptions.MockEmail.Should().BeFalse();
        capturedOptions.SenderEmail.Should().BeNull();
    }

    [Fact]
    public async Task InvokeAsync_WithEntraFlag_SetsIdpToEntra()
    {
        // Arrange
        SyncCliOptions? capturedOptions = null;
        var sp = CreateMockServiceProvider();

        var cmd = SyncCommand.Create(opts =>
        {
            capturedOptions = opts;
            return sp;
        });

        // Act
        var exitCode = await cmd.InvokeAsync([
            "--entra",
            "--tenant-domain", "sandbox.onmicrosoft.com",
            "--sender-email", "no-reply@sandbox.onmicrosoft.com",
            "--mock-email"
        ]);

        // Assert
        exitCode.Should().Be(0);
        capturedOptions.Should().NotBeNull();
        capturedOptions!.Idp.Should().Be("entra");
        capturedOptions.TenantDomain.Should().Be("sandbox.onmicrosoft.com");
        capturedOptions.SenderEmail.Should().Be("no-reply@sandbox.onmicrosoft.com");
        capturedOptions.MockEmail.Should().BeTrue();
    }

    [Theory]
    [InlineData("entra")]
    [InlineData("entra-id")]
    [InlineData("entraid")]
    [InlineData("azure")]
    public async Task InvokeAsync_WithIdpOptionVariations_NormalizesToEntra(string idpOptionValue)
    {
        // Arrange
        SyncCliOptions? capturedOptions = null;
        var sp = CreateMockServiceProvider();

        var cmd = SyncCommand.Create(opts =>
        {
            capturedOptions = opts;
            return sp;
        });

        // Act
        var exitCode = await cmd.InvokeAsync(["--idp", idpOptionValue]);

        // Assert
        exitCode.Should().Be(0);
        capturedOptions!.Idp.Should().Be("entra");
    }

    [Fact]
    public async Task InvokeAsync_WithDryRun_ExecutesWithoutError()
    {
        // Arrange
        var sp = CreateMockServiceProvider();
        var cmd = SyncCommand.Create(_ => sp);

        // Act
        var exitCode = await cmd.InvokeAsync(["--dry-run"]);

        // Assert
        exitCode.Should().Be(0);
    }

    [Fact]
    public async Task InvokeAsync_WhenCircuitBreakerTrips_ReturnsExitCode2()
    {
        // Arrange
        var sp = CreateMockServiceProvider(tripCircuitBreaker: true);
        var cmd = SyncCommand.Create(_ => sp);

        // Act
        var exitCode = await cmd.InvokeAsync([]);

        // Assert
        exitCode.Should().Be(2);
    }

    [Fact]
    public async Task InvokeAsync_WithMutations_WhenPromptConfirmed_ExecutesReconciliation()
    {
        // Arrange
        var emp = CreateTestEmployee();
        var (sp, store) = CreateMockServiceProviderWithStore(employees: [emp]);
        var prompter = Substitute.For<IConsolePrompter>();
        prompter.IsInputRedirected.Returns(false);
        prompter.Confirm(Arg.Any<string>()).Returns(true);

        var cmd = SyncCommand.Create(_ => sp, prompter);

        // Act
        var exitCode = await cmd.InvokeAsync([]);

        // Assert
        exitCode.Should().Be(0);
        prompter.Received(1).Confirm(Arg.Any<string>());
        await store.Received(1).ApplyBatchMutationsAsync(Arg.Any<IReadOnlyList<DeltaAction>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_WithMutations_WhenPromptCancelled_DoesNotExecuteMutations()
    {
        // Arrange
        var emp = CreateTestEmployee();
        var (sp, store) = CreateMockServiceProviderWithStore(employees: [emp]);
        var prompter = Substitute.For<IConsolePrompter>();
        prompter.IsInputRedirected.Returns(false);
        prompter.Confirm(Arg.Any<string>()).Returns(false);

        var cmd = SyncCommand.Create(_ => sp, prompter);

        // Act
        var exitCode = await cmd.InvokeAsync([]);

        // Assert
        exitCode.Should().Be(0);
        prompter.Received(1).Confirm(Arg.Any<string>());
        await store.DidNotReceiveWithAnyArgs().ApplyBatchMutationsAsync(default!, default);
    }

    [Fact]
    public async Task InvokeAsync_WithMutations_WithYesFlag_BypassesPromptAndExecutes()
    {
        // Arrange
        var emp = CreateTestEmployee();
        var (sp, store) = CreateMockServiceProviderWithStore(employees: [emp]);
        var prompter = Substitute.For<IConsolePrompter>();

        var cmd = SyncCommand.Create(_ => sp, prompter);

        // Act
        var exitCode = await cmd.InvokeAsync(["--yes"]);

        // Assert
        exitCode.Should().Be(0);
        prompter.DidNotReceiveWithAnyArgs().Confirm(default!);
        await store.Received(1).ApplyBatchMutationsAsync(Arg.Any<IReadOnlyList<DeltaAction>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_WithMutations_WhenInputRedirectedWithoutYes_ReturnsExitCode1()
    {
        // Arrange
        var emp = CreateTestEmployee();
        var (sp, store) = CreateMockServiceProviderWithStore(employees: [emp]);
        var prompter = Substitute.For<IConsolePrompter>();
        prompter.IsInputRedirected.Returns(true);

        var cmd = SyncCommand.Create(_ => sp, prompter);

        // Act
        var exitCode = await cmd.InvokeAsync([]);

        // Assert
        exitCode.Should().Be(1);
        prompter.DidNotReceiveWithAnyArgs().Confirm(default!);
        await store.DidNotReceiveWithAnyArgs().ApplyBatchMutationsAsync(default!, default);
    }

    [Fact]
    public async Task InvokeAsync_WhenFactoryThrows_ReturnsExitCode1()
    {
        // Arrange
        var cmd = SyncCommand.Create((Func<SyncCliOptions, IServiceProvider>)(_ =>
            throw new InvalidOperationException("Failed to load rules")));

        // Act
        var exitCode = await cmd.InvokeAsync([]);

        // Assert
        exitCode.Should().Be(1);
    }
}
