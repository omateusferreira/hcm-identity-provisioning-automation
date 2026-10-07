using System.CommandLine;
using FluentAssertions;
using HcmIdentityProvisioning.Application.Models;
using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Application.UseCases;
using HcmIdentityProvisioning.Cli.Commands;
using HcmIdentityProvisioning.Cli.Utils;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace HcmIdentityProvisioning.Cli.Tests;

public class EnsureGroupsCommandTests
{
    private static (IServiceProvider sp, IIdentityStore store) CreateMockServiceProviderWithStore()
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

        return (services.BuildServiceProvider(), store);
    }

    private static IServiceProvider CreateMockServiceProvider() => CreateMockServiceProviderWithStore().sp;

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
        var exitCode = await cmd.InvokeAsync(["--rules", "rules.json", "--yes"]);

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
            "--tenant-domain", "sandbox.onmicrosoft.com",
            "--yes"
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
    public async Task InvokeAsync_WithoutYes_WhenPromptCancelled_DoesNotCreateGroups()
    {
        // Arrange
        var (sp, store) = CreateMockServiceProviderWithStore();
        var prompter = Substitute.For<IConsolePrompter>();
        prompter.IsInputRedirected.Returns(false);
        prompter.Confirm(Arg.Any<string>()).Returns(false);

        var cmd = EnsureGroupsCommand.Create(_ => sp, prompter);

        // Act
        var exitCode = await cmd.InvokeAsync([]);

        // Assert
        exitCode.Should().Be(0);
        prompter.Received(1).Confirm(Arg.Any<string>());
        await store.DidNotReceiveWithAnyArgs().CreateManagedGroupAsync(default!, default, default);
    }

    [Fact]
    public async Task InvokeAsync_WithoutYes_WhenPromptConfirmed_CreatesGroups()
    {
        // Arrange
        var (sp, store) = CreateMockServiceProviderWithStore();
        var prompter = Substitute.For<IConsolePrompter>();
        prompter.IsInputRedirected.Returns(false);
        prompter.Confirm(Arg.Any<string>()).Returns(true);

        var cmd = EnsureGroupsCommand.Create(_ => sp, prompter);

        // Act
        var exitCode = await cmd.InvokeAsync([]);

        // Assert
        exitCode.Should().Be(0);
        prompter.Received(1).Confirm(Arg.Any<string>());
        await store.Received(2).CreateManagedGroupAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_WithYesFlag_BypassesPromptAndCreatesGroups()
    {
        // Arrange
        var (sp, store) = CreateMockServiceProviderWithStore();
        var prompter = Substitute.For<IConsolePrompter>();

        var cmd = EnsureGroupsCommand.Create(_ => sp, prompter);

        // Act
        var exitCode = await cmd.InvokeAsync(["--yes"]);

        // Assert
        exitCode.Should().Be(0);
        prompter.DidNotReceiveWithAnyArgs().Confirm(default!);
        await store.Received(2).CreateManagedGroupAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_WhenInputRedirectedWithoutYes_ReturnsExitCode1()
    {
        // Arrange
        var (sp, store) = CreateMockServiceProviderWithStore();
        var prompter = Substitute.For<IConsolePrompter>();
        prompter.IsInputRedirected.Returns(true);

        var cmd = EnsureGroupsCommand.Create(_ => sp, prompter);

        // Act
        var exitCode = await cmd.InvokeAsync([]);

        // Assert
        exitCode.Should().Be(1);
        prompter.DidNotReceiveWithAnyArgs().Confirm(default!);
        await store.DidNotReceiveWithAnyArgs().CreateManagedGroupAsync(default!, default, default);
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
