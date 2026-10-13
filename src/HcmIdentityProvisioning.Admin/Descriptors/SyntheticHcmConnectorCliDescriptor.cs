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
        description: "Path to synthetic-employees.json file (defaults to application bundle or FIXTURES_FILE_PATH).")
    {
        Arity = ArgumentArity.ZeroOrOne
    };

    public IReadOnlyList<Option> GetCliOptions() => [_fixturesOption];

    public void ConfigureServices(IServiceCollection services, ParseResult parseResult, SyncCliOptions baseOptions)
    {
        var hasOption = parseResult.HasOption(_fixturesOption);
        var fixturesFile = parseResult.GetValueForOption(_fixturesOption);
        var customPath = fixturesFile?.FullName ?? (!string.IsNullOrWhiteSpace(baseOptions.FixturesPath) ? baseOptions.FixturesPath : null);

        if (hasOption || !string.IsNullOrWhiteSpace(baseOptions.FixturesPath))
        {
            var resolvedPath = PathResolver.ResolveFixturesPath(customPath);
            services.AddSyntheticHcmConnector(resolvedPath);
        }
    }
}
