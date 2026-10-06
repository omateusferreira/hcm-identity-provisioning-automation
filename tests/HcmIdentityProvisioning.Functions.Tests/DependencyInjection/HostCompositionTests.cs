using FluentAssertions;
using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Application.UseCases;
using HcmIdentityProvisioning.Functions.Functions;
using HcmIdentityProvisioning.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

        services.AddHcmProvisioningCore(syncSettings, rulesJsonPath);
        services.AddSyntheticHcmConnector(dummyFixturesPath);
        services.AddInMemoryIdentityStore();
        services.AddTransient<SyncTimerFunction>();

        using var provider = services.BuildServiceProvider();
        var func = provider.GetService<SyncTimerFunction>();

        func.Should().NotBeNull();
        provider.GetService<ReconcileBatchUseCase>().Should().NotBeNull();
    }
}
