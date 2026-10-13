using System.CommandLine;
using System.CommandLine.Parsing;
using HcmIdentityProvisioning.Admin.Commands;
using HcmIdentityProvisioning.Admin.Extensibility;
using HcmIdentityProvisioning.Admin.Utils;
using HcmIdentityProvisioning.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace HcmIdentityProvisioning.Admin.Descriptors;

public sealed class SyntheticHcmConnectorCliDescriptor : IHcmConnectorCliDescriptor
{
    public string ConnectorKey => "synthetic";
    public string Description => "Synthetic In-Memory HCM Connector (Fixture based)";

    private readonly Option<FileInfo?> _fixturesOption = new(
        name: "--fixtures",
        description: "Path to synthetic-employees.json file (defaults to application bundle or FIXTURES_FILE_PATH).");

    public IReadOnlyList<Option> GetCliOptions() => [_fixturesOption];

    public void ConfigureServices(IServiceCollection services, ParseResult parseResult, SyncCliOptions baseOptions)
    {
        var fixturesFile = parseResult.GetValueForOption(_fixturesOption);
        var fixturesPath = fixturesFile?.FullName ?? baseOptions.FixturesPath;
        var resolvedPath = PathResolver.ResolveFixturesPath(fixturesPath);
        services.AddSyntheticHcmConnector(resolvedPath);
    }
}
