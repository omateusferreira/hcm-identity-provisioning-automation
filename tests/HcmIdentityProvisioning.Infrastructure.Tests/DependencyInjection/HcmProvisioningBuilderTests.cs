using FluentAssertions;
using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Application.UseCases;
using HcmIdentityProvisioning.Domain.Common;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Domain.ValueObjects;
using HcmIdentityProvisioning.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace HcmIdentityProvisioning.Infrastructure.Tests.DependencyInjection;

public class HcmProvisioningBuilderTests
{
    private class TestCustomConnector : IHcmConnector
    {
        public Task<PagedResult<Employee>> GetEmployeesPageAsync(int pageNumber, int pageSize, CancellationToken ct = default) =>
            Task.FromResult(new PagedResult<Employee>(Array.Empty<Employee>(), pageNumber, pageSize, 0, false));
    }

    private class TestOptions
    {
        public string ApiKey { get; set; } = string.Empty;
    }

    private class TestConfigurableConnector : IHcmConnector
    {
        public TestOptions Options { get; }
        public TestConfigurableConnector(IOptions<TestOptions> options) => Options = options.Value;

        public Task<PagedResult<Employee>> GetEmployeesPageAsync(int pageNumber, int pageSize, CancellationToken ct = default) =>
            Task.FromResult(new PagedResult<Employee>(Array.Empty<Employee>(), pageNumber, pageSize, 0, false));
    }

    private class TestCredentialDelivery : ICredentialDeliveryService
    {
        public Task DeliverInitialCredentialsAsync(Employee employee, UserPrincipalName upn, string temporaryPassword, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    [Fact]
    public void AddHcmProvisioning_RegistersCoreServicesAndReturnsBuilder()
    {
        var services = new ServiceCollection();
        var settings = new SyncSettings { TenantDomain = "contoso.com" };

        var builder = services.AddHcmProvisioning(settings, "Rules/rules.json");

        builder.Should().NotBeNull();
        builder.Services.Should().BeSameAs(services);
        builder.Settings.Should().BeSameAs(settings);
    }

    [Fact]
    public void AddConnector_Generic_RegistersConnectorAndResolvesUseCases()
    {
        var services = new ServiceCollection();
        var settings = new SyncSettings { TenantDomain = "contoso.com" };

        services.AddHcmProvisioning(settings, "Rules/rules.json")
            .AddInMemoryStore()
            .AddConnector<TestCustomConnector>();

        var sp = services.BuildServiceProvider();
        var connector = sp.GetRequiredService<IHcmConnector>();
        connector.Should().BeOfType<TestCustomConnector>();

        var useCase = sp.GetRequiredService<ReconcileBatchUseCase>();
        useCase.Should().NotBeNull();
    }

    [Fact]
    public void AddConnector_WithOptions_BindsOptionsCorrectly()
    {
        var services = new ServiceCollection();
        var settings = new SyncSettings { TenantDomain = "contoso.com" };

        services.AddHcmProvisioning(settings, "Rules/rules.json")
            .AddInMemoryStore()
            .AddConnector<TestConfigurableConnector, TestOptions>(opts =>
            {
                opts.ApiKey = "secret-token-123";
            });

        var sp = services.BuildServiceProvider();
        var connector = sp.GetRequiredService<IHcmConnector>() as TestConfigurableConnector;
        connector.Should().NotBeNull();
        connector!.Options.ApiKey.Should().Be("secret-token-123");
    }

    [Fact]
    public void AddSyntheticConnector_RegistersSyntheticConnector()
    {
        var services = new ServiceCollection();
        var settings = new SyncSettings { TenantDomain = "contoso.com" };

        var builder = services.AddHcmProvisioning(settings, "Rules/rules.json")
            .AddSyntheticConnector("Fixtures/sample-employees.json");

        builder.Should().NotBeNull();
    }

    [Fact]
    public void AddGenericRestConnector_RegistersRestConnector()
    {
        var services = new ServiceCollection();
        var settings = new SyncSettings { TenantDomain = "contoso.com" };

        var builder = services.AddHcmProvisioning(settings, "Rules/rules.json")
            .AddGenericRestConnector(opts => opts.BaseUrl = "https://api.example.com");

        builder.Should().NotBeNull();
    }

    [Fact]
    public void AddEntraIdStore_RegistersEntraIdStore()
    {
        var services = new ServiceCollection();
        var settings = new SyncSettings { TenantDomain = "contoso.com" };

        var builder = services.AddHcmProvisioning(settings, "Rules/rules.json")
            .AddEntraIdStore(opts => opts.TenantDomain = "contoso.onmicrosoft.com");

        builder.Should().NotBeNull();
    }

    [Fact]
    public void AddInMemoryStore_RegistersInMemoryStore()
    {
        var services = new ServiceCollection();
        var settings = new SyncSettings { TenantDomain = "contoso.com" };

        services.AddHcmProvisioning(settings, "Rules/rules.json")
            .AddInMemoryStore(store =>
            {
                store.SeedGroup(Guid.NewGuid(), "grp-iam-engineers");
            });

        var sp = services.BuildServiceProvider();
        var store = sp.GetRequiredService<IIdentityStore>();
        store.Should().NotBeNull();
    }

    [Fact]
    public void AddCredentialDelivery_RegistersCustomDeliveryService()
    {
        var services = new ServiceCollection();
        var settings = new SyncSettings { TenantDomain = "contoso.com" };

        services.AddHcmProvisioning(settings, "Rules/rules.json")
            .AddCredentialDelivery<TestCredentialDelivery>();

        var sp = services.BuildServiceProvider();
        var delivery = sp.GetRequiredService<ICredentialDeliveryService>();
        delivery.Should().BeOfType<TestCredentialDelivery>();
    }

    [Fact]
    public void AddGraphEmailCredentialDelivery_RegistersEmailDeliveryService()
    {
        var services = new ServiceCollection();
        var settings = new SyncSettings { TenantDomain = "contoso.com" };

        var builder = services.AddHcmProvisioning(settings, "Rules/rules.json")
            .AddGraphEmailCredentialDelivery(opts =>
            {
                opts.SenderEmail = "admin@contoso.com";
            });

        builder.Should().NotBeNull();
    }
}
