using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Application.Services;
using HcmIdentityProvisioning.Application.UseCases;
using HcmIdentityProvisioning.Domain.Policies;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Infrastructure.Connectors.Synthetic;
using HcmIdentityProvisioning.Infrastructure.Graph;
using HcmIdentityProvisioning.Infrastructure.Notifications;
using HcmIdentityProvisioning.Infrastructure.Policies;
using HcmIdentityProvisioning.Infrastructure.Rules;
using HcmIdentityProvisioning.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;

namespace HcmIdentityProvisioning.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddHcmProvisioningCore(
        this IServiceCollection services,
        SyncSettings settings,
        string rulesJsonPath)
    {
        services.AddSingleton(settings);
        services.AddSingleton<ISecurePasswordGenerator, SecurePasswordGenerator>();
        services.AddSingleton<ICircuitBreaker>(_ => new DisablementCircuitBreaker(settings.MaxDisablementPercentage, settings.MaxDisablementCount));
        services.AddSingleton<IRulesEngine>(_ => MicrosoftRulesEngineAdapter.FromFile(rulesJsonPath));
        services.AddSingleton<IdentityReconciliationService>();
        services.AddTransient<ReconcileBatchUseCase>();
        services.AddTransient<DryRunAuditUseCase>();

        return services;
    }

    public static IServiceCollection AddSyntheticHcmConnector(
        this IServiceCollection services,
        string fixturesPath)
    {
        services.AddSingleton<IHcmConnector>(_ => SyntheticHcmConnector.FromFixturesFile(fixturesPath));
        return services;
    }

    public static IServiceCollection AddInMemoryIdentityStore(
        this IServiceCollection services,
        Action<InMemoryIdentityStore>? seedAction = null)
    {
        var store = new InMemoryIdentityStore();
        seedAction?.Invoke(store);
        services.AddSingleton<IIdentityStore>(store);
        services.AddSingleton<ICredentialDeliveryService, MockCredentialDeliveryService>();
        return services;
    }
}
