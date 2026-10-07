using System.CommandLine;
using FluentAssertions;
using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Application.Services;
using HcmIdentityProvisioning.Application.UseCases;
using HcmIdentityProvisioning.Cli.Commands;
using HcmIdentityProvisioning.Domain.Actions;
using HcmIdentityProvisioning.Domain.Common;
using HcmIdentityProvisioning.Domain.Entities;
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
    private static IServiceProvider CreateMockServiceProvider(bool tripCircuitBreaker = false)
    {
        var services = new ServiceCollection();

        var connector = Substitute.For<IHcmConnector>();
        connector.GetEmployeesPageAsync(1, 50, Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Employee>([], 1, 50, 0, false));

        var store = Substitute.For<IIdentityStore>();
        store.GetManagedGroupsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, ManagedGroup>>(new Dictionary<string, ManagedGroup>()));
        store.GetUsersByEmployeeIdsAsync(Arg.Any<IEnumerable<EmployeeId>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<EmployeeId, EntraUser>>(new Dictionary<EmployeeId, EntraUser>()));

        var rulesEngine = Substitute.For<IRulesEngine>();
        var pwdGen = Substitute.For<ISecurePasswordGenerator>();
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

        return services.BuildServiceProvider();
    }

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
