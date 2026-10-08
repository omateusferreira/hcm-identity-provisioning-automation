using FluentAssertions;
using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Application.UseCases;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Functions.Functions;
using HcmIdentityProvisioning.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using Xunit;

namespace HcmIdentityProvisioning.Functions.Tests.DependencyInjection;

public class HostCompositionTests
{
    [Fact]
    public void BuildServiceProvider_CanResolveSyncTimerFunctionAndUseCases()
    {
        var services = new ServiceCollection();

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        var syncSettings = new SyncSettings
        {
            TenantDomain = "company.onmicrosoft.com",
            ManagedGroupPrefix = "grp-iam-"
        };

        var rulesJsonPath = Path.Combine(AppContext.BaseDirectory, "Rules", "rules.json");
        if (!File.Exists(rulesJsonPath))
        {
            var fallback = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "HcmIdentityProvisioning.Infrastructure", "Rules", "rules.json"));
            rulesJsonPath = File.Exists(fallback) ? fallback : rulesJsonPath;
        }

        var dummyFixturesPath = Path.Combine(AppContext.BaseDirectory, "dummy-fixtures.json");
        if (!File.Exists(dummyFixturesPath))
        {
            File.WriteAllText(dummyFixturesPath, "[]");
        }

        services.AddHcmProvisioning(syncSettings, rulesJsonPath)
            .AddSyntheticConnector(dummyFixturesPath)
            .AddInMemoryStore();
        services.AddTransient<SyncTimerFunction>();

        using var provider = services.BuildServiceProvider();
        var func = provider.GetService<SyncTimerFunction>();

        func.Should().NotBeNull();
        provider.GetService<ReconcileBatchUseCase>().Should().NotBeNull();
    }

    [Fact]
    public void BuildServiceProvider_WithProductionAdapters_ResolvesSyncTimerFunctionAndUseCases()
    {
        var services = new ServiceCollection();

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        var syncSettings = new SyncSettings
        {
            TenantDomain = "company.onmicrosoft.com",
            ManagedGroupPrefix = "grp-iam-"
        };

        var rulesJsonPath = Path.Combine(AppContext.BaseDirectory, "Rules", "rules.json");
        if (!File.Exists(rulesJsonPath))
        {
            var fallback = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "HcmIdentityProvisioning.Infrastructure", "Rules", "rules.json"));
            rulesJsonPath = File.Exists(fallback) ? fallback : rulesJsonPath;
        }

        var mockGraphClient = new GraphServiceClient(new HttpClient(), new AnonymousAuthenticationProvider());
        services.AddSingleton(mockGraphClient);

        services.AddHcmProvisioning(syncSettings, rulesJsonPath)
            .AddEntraIdStore(options =>
            {
                options.TenantDomain = "company.onmicrosoft.com";
                options.ManagedGroupPrefix = "grp-iam-";
            })
            .AddGenericRestConnector(options =>
            {
                options.BaseUrl = "https://api.hcm.example.com";
                options.TimeoutSeconds = 10;
            })
            .AddGraphEmailCredentialDelivery(options =>
            {
                options.SenderEmail = "no-reply@company.com";
            });
        services.AddTransient<SyncTimerFunction>();

        using var provider = services.BuildServiceProvider();
        var timerFunc = provider.GetService<SyncTimerFunction>();
        var reconcileUseCase = provider.GetService<ReconcileBatchUseCase>();
        var credentialDelivery = provider.GetService<ICredentialDeliveryService>();

        timerFunc.Should().NotBeNull();
        reconcileUseCase.Should().NotBeNull();
        credentialDelivery.Should().NotBeNull();
    }
}
