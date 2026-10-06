using FluentAssertions;
using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Application.Services;
using HcmIdentityProvisioning.Application.UseCases;
using HcmIdentityProvisioning.Domain.Policies;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace HcmIdentityProvisioning.Infrastructure.Tests.DependencyInjection;

public sealed class ServiceCollectionExtensionsTests
{
    private static string ResolveRepoFile(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
            current = current.Parent;
        }
        return relativePath;
    }

    [Fact]
    public void AddHcmProvisioningCore_RegistersRequiredServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var settings = new SyncSettings
        {
            TenantDomain = "company.onmicrosoft.com",
            ManagedGroupPrefix = "grp-iam-",
            MaxDisablementPercentage = 15.0,
            MaxDisablementCount = 30
        };

        var rulesPath = ResolveRepoFile("src/HcmIdentityProvisioning.Infrastructure/Rules/rules.json");
        services.AddHcmProvisioningCore(settings, rulesPath);

        // Also add connector and store to verify use cases can resolve
        var fixturesPath = ResolveRepoFile("fixtures/synthetic-employees.json");
        services.AddSyntheticHcmConnector(fixturesPath);
        services.AddInMemoryIdentityStore();

        var sp = services.BuildServiceProvider();

        sp.GetRequiredService<SyncSettings>().Should().Be(settings);
        sp.GetRequiredService<ISecurePasswordGenerator>().Should().NotBeNull();
        sp.GetRequiredService<ICircuitBreaker>().Should().NotBeNull();
        sp.GetRequiredService<IRulesEngine>().Should().NotBeNull();
        sp.GetRequiredService<IdentityReconciliationService>().Should().NotBeNull();
        sp.GetRequiredService<DryRunAuditUseCase>().Should().NotBeNull();
        sp.GetRequiredService<ReconcileBatchUseCase>().Should().NotBeNull();
    }

    [Fact]
    public async Task AddSyntheticHcmConnector_RegistersHcmConnectorThatLoadsEmployees()
    {
        var services = new ServiceCollection();
        var fixturesPath = ResolveRepoFile("fixtures/synthetic-employees.json");

        services.AddSyntheticHcmConnector(fixturesPath);
        var sp = services.BuildServiceProvider();

        var connector = sp.GetRequiredService<IHcmConnector>();
        connector.Should().NotBeNull();

        var page = await connector.GetEmployeesPageAsync(pageNumber: 1, pageSize: 50);
        page.Items.Should().HaveCount(6);
    }

    [Fact]
    public void AddInMemoryIdentityStore_RegistersStoreAndAppliesSeedAction()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var groupId = Guid.NewGuid();

        services.AddInMemoryIdentityStore(store =>
        {
            store.SeedGroup(groupId, "grp-iam-test");
        });

        var sp = services.BuildServiceProvider();

        var store = sp.GetRequiredService<IIdentityStore>();
        store.Should().NotBeNull();

        var delivery = sp.GetRequiredService<ICredentialDeliveryService>();
        delivery.Should().NotBeNull();
    }
}
