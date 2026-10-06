using System.CommandLine;
using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Cli.Commands;
using HcmIdentityProvisioning.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

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

// Robust path resolution for rules and fixtures
static string ResolvePath(string relativePath)
{
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current != null)
    {
        var candidate = Path.Combine(current.FullName, relativePath);
        if (File.Exists(candidate))
            return candidate;
        current = current.Parent;
    }
    return relativePath;
}

var rulesPath = ResolvePath("src/HcmIdentityProvisioning.Infrastructure/Rules/rules.json");
var fixturesPath = ResolvePath("fixtures/synthetic-employees.json");

services.AddHcmProvisioningCore(settings, rulesPath);
services.AddSyntheticHcmConnector(fixturesPath);
services.AddInMemoryIdentityStore(store =>
{
    store.SeedGroup(Guid.Parse("11111111-1111-1111-1111-111111111111"), "grp-iam-engineering");
    store.SeedGroup(Guid.Parse("22222222-2222-2222-2222-222222222222"), "grp-iam-finance");
    store.SeedGroup(Guid.Parse("33333333-3333-3333-3333-333333333333"), "grp-iam-all-staff");
});

var sp = services.BuildServiceProvider();

var rootCommand = new RootCommand("HCM to Microsoft Entra ID Provisioning & Lifecycle Engine");
rootCommand.AddCommand(SyncCommand.Create(sp));
rootCommand.AddCommand(ValidateRulesCommand.Create());

var exitCode = await rootCommand.InvokeAsync(args);
return Environment.ExitCode != 0 ? Environment.ExitCode : exitCode;
