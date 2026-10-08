using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Infrastructure.Connectors.Rest;
using HcmIdentityProvisioning.Infrastructure.Graph;
using HcmIdentityProvisioning.Infrastructure.Notifications;
using Microsoft.Extensions.DependencyInjection;
using GraphEmailOptions = HcmIdentityProvisioning.Infrastructure.Notifications.GraphEmailDeliveryOptions;

namespace HcmIdentityProvisioning.Infrastructure.DependencyInjection;

public sealed class HcmProvisioningBuilder : IHcmProvisioningBuilder
{
    public IServiceCollection Services { get; }
    public SyncSettings Settings { get; }

    public HcmProvisioningBuilder(IServiceCollection services, SyncSettings settings)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public IHcmProvisioningBuilder AddConnector<TConnector>()
        where TConnector : class, IHcmConnector
    {
        Services.AddSingleton<IHcmConnector, TConnector>();
        return this;
    }

    public IHcmProvisioningBuilder AddConnector<TConnector, TOptions>(Action<TOptions> configureOptions)
        where TConnector : class, IHcmConnector
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(configureOptions);
        Services.Configure(configureOptions);
        Services.AddSingleton<IHcmConnector, TConnector>();
        return this;
    }

    public IHcmProvisioningBuilder AddSyntheticConnector(string fixturesPath)
    {
        Services.AddSyntheticHcmConnector(fixturesPath);
        return this;
    }

    public IHcmProvisioningBuilder AddGenericRestConnector(Action<RestHcmConnectorOptions>? configureOptions = null)
    {
        Services.AddGenericRestHcmConnector(configureOptions);
        return this;
    }

    public IHcmProvisioningBuilder AddEntraIdStore(Action<EntraIdGraphOptions>? configureOptions = null)
    {
        Services.AddEntraIdGraphAdapter(configureOptions);
        return this;
    }

    public IHcmProvisioningBuilder AddInMemoryStore(Action<InMemoryIdentityStore>? seedAction = null)
    {
        Services.AddInMemoryIdentityStore(seedAction);
        return this;
    }

    public IHcmProvisioningBuilder AddCredentialDelivery<TDelivery>()
        where TDelivery : class, ICredentialDeliveryService
    {
        Services.AddSingleton<ICredentialDeliveryService, TDelivery>();
        return this;
    }

    public IHcmProvisioningBuilder AddGraphEmailCredentialDelivery(Action<GraphEmailOptions> configureOptions)
    {
        Services.AddGraphEmailCredentialDeliveryService(configureOptions);
        return this;
    }
}
