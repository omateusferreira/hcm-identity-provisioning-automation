using System.CommandLine;
using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Cli.Commands;
using HcmIdentityProvisioning.Cli.Utils;
using HcmIdentityProvisioning.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

static IServiceProvider BuildServiceProvider(string? customRules, string? customFixtures)
{
    var services = new ServiceCollection();
    services.AddLogging(builder =>
    {
        builder.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Warning;
        }).SetMinimumLevel(LogLevel.Warning);
    });

    var settings = new SyncSettings
    {
        TenantDomain = "company.onmicrosoft.com",
        ManagedGroupPrefix = "grp-iam-"
    };

    var rulesPath = PathResolver.ResolveRulesPath(customRules);
    var fixturesPath = PathResolver.ResolveFixturesPath(customFixtures);

    services.AddHcmProvisioningCore(settings, rulesPath);
    services.AddSyntheticHcmConnector(fixturesPath);
    services.AddInMemoryIdentityStore(store =>
    {
        store.SeedGroup(Guid.Parse("11111111-1111-1111-1111-111111111111"), "grp-iam-engineering");
        store.SeedGroup(Guid.Parse("22222222-2222-2222-2222-222222222222"), "grp-iam-finance");
        store.SeedGroup(Guid.Parse("33333333-3333-3333-3333-333333333333"), "grp-iam-all-staff");
    });

    return services.BuildServiceProvider();
}

var rootCommand = new RootCommand("HCM to Microsoft Entra ID Provisioning & Lifecycle Engine");
rootCommand.AddCommand(SyncCommand.Create(BuildServiceProvider));
rootCommand.AddCommand(ValidateRulesCommand.Create());

var exitCode = await rootCommand.InvokeAsync(args);
return Environment.ExitCode != 0 ? Environment.ExitCode : exitCode;
