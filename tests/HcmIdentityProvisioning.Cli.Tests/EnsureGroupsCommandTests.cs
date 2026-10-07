using System.CommandLine;
using FluentAssertions;
using HcmIdentityProvisioning.Application.Models;
using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Application.UseCases;
using HcmIdentityProvisioning.Cli.Commands;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace HcmIdentityProvisioning.Cli.Tests;

public class EnsureGroupsCommandTests
{
    private static IServiceProvider CreateMockServiceProvider()
    {
        var services = new ServiceCollection();

        var rulesEngine = Substitute.For<IRulesEngine>();
        rulesEngine.GetDeclaredGroupNames()
            .Returns(new HashSet<string> { "grp-iam-engineering", "grp-iam-finance" });

        var store = Substitute.For<IIdentityStore>();
        store.GetManagedGroupsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, ManagedGroup>>(new Dictionary<string, ManagedGroup>()));
        store.CreateManagedGroupAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => new ManagedGroup(Guid.NewGuid(), callInfo.ArgAt<string>(0)));

        var settings = new SyncSettings
        {
            TenantDomain = "company.onmicrosoft.com",
            ManagedGroupPrefix = "grp-iam-"
        };

        var useCase = new EnsureManagedGroupsUseCase(
            rulesEngine,
            store,
            settings,
            NullLogger<EnsureManagedGroupsUseCase>.Instance
        );

        services.AddScoped(_ => useCase);

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task InvokeAsync_WithDefaultOptions_PassesInMemoryDefaultsToFactory()
    {
        // Arrange
        SyncCliOptions? capturedOptions = null;
        var sp = CreateMockServiceProvider();

        var cmd = EnsureGroupsCommand.Create(opts =>
        {
            capturedOptions = opts;
            return sp;
        });

        // Act
        var exitCode = await cmd.InvokeAsync(["--rules", "rules.json"]);

        // Assert
        exitCode.Should().Be(0);
        capturedOptions.Should().NotBeNull();
        capturedOptions!.Idp.Should().Be("in-memory");
        capturedOptions.TenantDomain.Should().Be("company.onmicrosoft.com");
    }

    [Fact]
    public async Task InvokeAsync_WithEntraFlag_SetsIdpToEntra()
    {
        // Arrange
        SyncCliOptions? capturedOptions = null;
        var sp = CreateMockServiceProvider();

        var cmd = EnsureGroupsCommand.Create(opts =>
        {
            capturedOptions = opts;
            return sp;
        });

        // Act
        var exitCode = await cmd.InvokeAsync([
            "--entra",
            "--tenant-domain", "sandbox.onmicrosoft.com"
        ]);

        // Assert
        exitCode.Should().Be(0);
        capturedOptions.Should().NotBeNull();
        capturedOptions!.Idp.Should().Be("entra");
        capturedOptions.TenantDomain.Should().Be("sandbox.onmicrosoft.com");
    }

    [Fact]
    public async Task InvokeAsync_WithDryRun_ExecutesWithoutError()
    {
        // Arrange
        var sp = CreateMockServiceProvider();
        var cmd = EnsureGroupsCommand.Create(_ => sp);

        // Act
        var exitCode = await cmd.InvokeAsync(["--dry-run"]);

        // Assert
        exitCode.Should().Be(0);
    }

    [Fact]
    public async Task InvokeAsync_WhenFactoryThrows_ReturnsExitCode1()
    {
        // Arrange
        var cmd = EnsureGroupsCommand.Create((Func<SyncCliOptions, IServiceProvider>)(_ =>
            throw new InvalidOperationException("Failed to load rules")));

        // Act
        var exitCode = await cmd.InvokeAsync([]);

        // Assert
        exitCode.Should().Be(1);
    }
}
