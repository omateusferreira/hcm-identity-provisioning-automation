using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Infrastructure.Connectors.Rest;
using HcmIdentityProvisioning.Infrastructure.Graph;
using HcmIdentityProvisioning.Infrastructure.Notifications;
using Microsoft.Extensions.DependencyInjection;
using GraphEmailOptions = HcmIdentityProvisioning.Infrastructure.Notifications.GraphEmailDeliveryOptions;

namespace HcmIdentityProvisioning.Infrastructure.DependencyInjection;

public interface IHcmProvisioningBuilder
{
    IServiceCollection Services { get; }
    SyncSettings Settings { get; }

    IHcmProvisioningBuilder AddConnector<TConnector>()
        where TConnector : class, IHcmConnector;

    IHcmProvisioningBuilder AddConnector<TConnector, TOptions>(Action<TOptions> configureOptions)
        where TConnector : class, IHcmConnector
        where TOptions : class;

    IHcmProvisioningBuilder AddSyntheticConnector(string fixturesPath);

    IHcmProvisioningBuilder AddGenericRestConnector(Action<RestHcmConnectorOptions>? configureOptions = null);

    IHcmProvisioningBuilder AddEntraIdStore(Action<EntraIdGraphOptions>? configureOptions = null);

    IHcmProvisioningBuilder AddInMemoryStore(Action<InMemoryIdentityStore>? seedAction = null);

    IHcmProvisioningBuilder AddCredentialDelivery<TDelivery>()
        where TDelivery : class, ICredentialDeliveryService;

    IHcmProvisioningBuilder AddGraphEmailCredentialDelivery(Action<GraphEmailOptions> configureOptions);
}
