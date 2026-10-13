using System.CommandLine;
using System.CommandLine.Parsing;
using HcmIdentityProvisioning.Admin.Commands;
using HcmIdentityProvisioning.Admin.Extensibility;
using HcmIdentityProvisioning.Infrastructure.Connectors.Rest;
using HcmIdentityProvisioning.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace HcmIdentityProvisioning.Admin.Descriptors;

public sealed class GenericRestHcmConnectorCliDescriptor : IHcmConnectorCliDescriptor
{
    public string ConnectorKey => "rest";
    public string Description => "Generic REST HCM Connector";

    private readonly Option<string?> _restBaseUrlOption = new(
        name: "--rest-url",
        description: "Base API URL for Generic REST HCM Connector.");

    private readonly Option<string?> _restAuthTokenOption = new(
        name: "--rest-token",
        description: "Bearer authentication token for Generic REST HCM Connector.");

    public IReadOnlyList<Option> GetCliOptions() => [_restBaseUrlOption, _restAuthTokenOption];

    public void ConfigureServices(IServiceCollection services, ParseResult parseResult, SyncCliOptions baseOptions)
    {
        var baseUrl = parseResult.GetValueForOption(_restBaseUrlOption);
        var token = parseResult.GetValueForOption(_restAuthTokenOption);

        if (!string.IsNullOrWhiteSpace(baseUrl) || !string.IsNullOrWhiteSpace(token))
        {
            services.AddGenericRestHcmConnector(opts =>
            {
                if (!string.IsNullOrWhiteSpace(baseUrl)) opts.BaseUrl = baseUrl;
                if (!string.IsNullOrWhiteSpace(token))
                {
                    opts.AuthScheme = HcmAuthScheme.Bearer;
                    opts.BearerToken = token;
                }
            });
        }
    }
}
