using System.CommandLine;
using System.CommandLine.Parsing;
using HcmIdentityProvisioning.Admin.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace HcmIdentityProvisioning.Admin.Extensibility;

public interface IHcmConnectorCliDescriptor
{
    string ConnectorKey { get; }
    string Description { get; }
    IReadOnlyList<Option> GetCliOptions();
    void ConfigureServices(IServiceCollection services, ParseResult parseResult, SyncCliOptions baseOptions);
}
