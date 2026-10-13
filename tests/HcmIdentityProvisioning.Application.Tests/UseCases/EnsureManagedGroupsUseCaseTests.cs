using FluentAssertions;
using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Application.UseCases;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Ports;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace HcmIdentityProvisioning.Application.Tests.UseCases;

public class EnsureManagedGroupsUseCaseTests
{
    private readonly IRulesEngine _rulesEngine = Substitute.For<IRulesEngine>();
    private readonly IIdentityStore _identityStore = Substitute.For<IIdentityStore>();
    private readonly SyncSettings _settings = new()
    {
        ManagedGroupPrefix = "grp-iam-",
        TenantDomain = "company.onmicrosoft.com"
    };

    private EnsureManagedGroupsUseCase CreateSut() =>
        new(_rulesEngine, _identityStore, _settings, NullLogger<EnsureManagedGroupsUseCase>.Instance);

    [Fact]
    public async Task ExecuteAsync_WhenAllGroupsExist_DoesNotCreateAnyGroup()
    {
        // Arrange
        _rulesEngine.GetDeclaredGroupNames()
            .Returns(new HashSet<string> { "grp-iam-engineering", "grp-iam-finance" });

        var existingGroups = new Dictionary<string, ManagedGroup>(StringComparer.OrdinalIgnoreCase)
        {
            ["grp-iam-engineering"] = new ManagedGroup(Guid.NewGuid(), "grp-iam-engineering"),
            ["grp-iam-finance"] = new ManagedGroup(Guid.NewGuid(), "grp-iam-finance")
        };
        _identityStore.GetManagedGroupsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, ManagedGroup>>(existingGroups));

        var sut = CreateSut();

        // Act
        var report = await sut.ExecuteAsync(dryRun: false);

        // Assert
        report.DryRun.Should().BeFalse();
        report.ExistingGroups.Should().BeEquivalentTo(["grp-iam-engineering", "grp-iam-finance"]);
        report.MissingGroups.Should().BeEmpty();
        report.CreatedGroups.Should().BeEmpty();

        await _identityStore.DidNotReceive().CreateManagedGroupAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenGroupsMissing_CreatesMissingGroups_AndReturnsReport()
    {
        // Arrange
        _rulesEngine.GetDeclaredGroupNames()
            .Returns(new HashSet<string> { "grp-iam-engineering", "grp-iam-finance", "grp-iam-all-staff" });

        var existingGroups = new Dictionary<string, ManagedGroup>(StringComparer.OrdinalIgnoreCase)
        {
            ["grp-iam-engineering"] = new ManagedGroup(Guid.NewGuid(), "grp-iam-engineering")
        };
        _identityStore.GetManagedGroupsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, ManagedGroup>>(existingGroups));

        _identityStore.CreateManagedGroupAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => new ManagedGroup(Guid.NewGuid(), callInfo.ArgAt<string>(0)));

        var sut = CreateSut();

        // Act
        var report = await sut.ExecuteAsync(dryRun: false);

        // Assert
        report.DryRun.Should().BeFalse();
        report.ExistingGroups.Should().BeEquivalentTo(["grp-iam-engineering"]);
        report.MissingGroups.Should().BeEquivalentTo(["grp-iam-finance", "grp-iam-all-staff"]);
        report.CreatedGroups.Should().BeEquivalentTo(["grp-iam-finance", "grp-iam-all-staff"]);

        await _identityStore.Received(1).CreateManagedGroupAsync(
            "grp-iam-finance", Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _identityStore.Received(1).CreateManagedGroupAsync(
            "grp-iam-all-staff", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WithDryRun_IdentifiesMissingGroupsWithoutCallingCreate()
    {
        // Arrange
        _rulesEngine.GetDeclaredGroupNames()
            .Returns(new HashSet<string> { "grp-iam-engineering", "grp-iam-finance" });

        var existingGroups = new Dictionary<string, ManagedGroup>(StringComparer.OrdinalIgnoreCase);
        _identityStore.GetManagedGroupsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, ManagedGroup>>(existingGroups));

        var sut = CreateSut();

        // Act
        var report = await sut.ExecuteAsync(dryRun: true);

        // Assert
        report.DryRun.Should().BeTrue();
        report.ExistingGroups.Should().BeEmpty();
        report.MissingGroups.Should().BeEquivalentTo(["grp-iam-engineering", "grp-iam-finance"]);
        report.CreatedGroups.Should().BeEmpty();

        await _identityStore.DidNotReceive().CreateManagedGroupAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_IgnoresGroupsWithoutManagedPrefix()
    {
        // Arrange: "other-group" does not start with "grp-iam-"
        _rulesEngine.GetDeclaredGroupNames()
            .Returns(new HashSet<string> { "grp-iam-engineering", "other-unmanaged-group" });

        var existingGroups = new Dictionary<string, ManagedGroup>(StringComparer.OrdinalIgnoreCase);
        _identityStore.GetManagedGroupsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, ManagedGroup>>(existingGroups));

        _identityStore.CreateManagedGroupAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => new ManagedGroup(Guid.NewGuid(), callInfo.ArgAt<string>(0)));

        var sut = CreateSut();

        // Act
        var report = await sut.ExecuteAsync(dryRun: false);

        // Assert
        report.MissingGroups.Should().ContainSingle().Which.Should().Be("grp-iam-engineering");
        report.CreatedGroups.Should().ContainSingle().Which.Should().Be("grp-iam-engineering");

        await _identityStore.DidNotReceive().CreateManagedGroupAsync(
            "other-unmanaged-group", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
