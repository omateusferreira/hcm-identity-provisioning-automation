using System.CommandLine;
using Azure.Identity;
using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Cli.Commands;
using HcmIdentityProvisioning.Cli.Utils;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Infrastructure.DependencyInjection;
using HcmIdentityProvisioning.Infrastructure.Graph;
using HcmIdentityProvisioning.Infrastructure.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Graph;

var isVerbose = args.Contains("--verbose", StringComparer.OrdinalIgnoreCase) || args.Contains("-v", StringComparer.OrdinalIgnoreCase);

IServiceProvider BuildServiceProvider(SyncCliOptions options)
{
    var services = new ServiceCollection();
    services.AddLogging(builder =>
    {
        builder.AddConsole(cOptions =>
        {
            cOptions.LogToStandardErrorThreshold = LogLevel.Warning;
        }).SetMinimumLevel(isVerbose ? LogLevel.Information : LogLevel.Warning);
    });

    var settings = new SyncSettings
    {
        TenantDomain = options.TenantDomain,
        ManagedGroupPrefix = "grp-iam-"
    };

    var rulesPath = PathResolver.ResolveRulesPath(options.RulesPath);
    var fixturesPath = PathResolver.ResolveFixturesPath(options.FixturesPath);

    services.AddHcmProvisioningCore(settings, rulesPath);
    services.AddSyntheticHcmConnector(fixturesPath);

    if (string.Equals(options.Idp, "entra", StringComparison.OrdinalIgnoreCase))
    {
        services.AddEntraIdGraphAdapter(graphOpts =>
        {
            graphOpts.TenantDomain = options.TenantDomain;
        });

        services.AddSingleton<GraphServiceClient>(_ => new GraphServiceClient(new DefaultAzureCredential()));

        if (options.MockEmail || string.IsNullOrWhiteSpace(options.SenderEmail))
        {
            services.AddSingleton<ICredentialDeliveryService, MockCredentialDeliveryService>();
        }
        else
        {
            services.AddGraphEmailCredentialDeliveryService(mailOpts =>
            {
                mailOpts.SenderEmail = options.SenderEmail;
            });
        }
    }
    else
    {
        services.AddInMemoryIdentityStore(store =>
        {
            store.SeedGroup(Guid.Parse("11111111-1111-1111-1111-111111111111"), "grp-iam-engineering");
            store.SeedGroup(Guid.Parse("22222222-2222-2222-2222-222222222222"), "grp-iam-finance");
            store.SeedGroup(Guid.Parse("33333333-3333-3333-3333-333333333333"), "grp-iam-all-staff");
        });
    }

    return services.BuildServiceProvider();
}

var rootCommand = new RootCommand("HCM to Microsoft Entra ID Provisioning & Lifecycle Engine");

var verboseOption = new Option<bool>(
    aliases: ["--verbose", "-v"],
    description: "Enable verbose diagnostic logging (LogLevel.Information).");
rootCommand.AddGlobalOption(verboseOption);

rootCommand.AddCommand(SyncCommand.Create(BuildServiceProvider));
rootCommand.AddCommand(EnsureGroupsCommand.Create(BuildServiceProvider));
rootCommand.AddCommand(ValidateRulesCommand.Create());

var exitCode = await rootCommand.InvokeAsync(args);
return Environment.ExitCode != 0 ? Environment.ExitCode : exitCode;

