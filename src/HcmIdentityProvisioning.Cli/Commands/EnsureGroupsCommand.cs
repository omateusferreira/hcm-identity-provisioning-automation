using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using HcmIdentityProvisioning.Application.Models;
using HcmIdentityProvisioning.Application.UseCases;
using Microsoft.Extensions.DependencyInjection;

namespace HcmIdentityProvisioning.Cli.Commands;

public static class EnsureGroupsCommand
{
    public static Command Create(IServiceProvider serviceProvider) =>
        Create(_ => serviceProvider);

    public static Command Create(Func<SyncCliOptions, IServiceProvider> serviceProviderFactory)
    {
        var dryRunOption = new Option<bool>(
            name: "--dry-run",
            description: "Simulate inspection and creation without modifying the Identity Provider.");

        var jsonLogsOption = new Option<bool>(
            name: "--json-logs",
            description: "Output audit report as structured JSON.");

        var rulesOption = new Option<FileInfo?>(
            name: "--rules",
            description: "Path to rules.json file (defaults to application bundle or RULES_FILE_PATH).");

        var idpOption = new Option<string>(
            name: "--idp",
            description: "Target Identity Provider: 'in-memory' (default) or 'entra' / 'entra-id'.",
            getDefaultValue: () => "in-memory");

        var entraOption = new Option<bool>(
            name: "--entra",
            description: "Shortcut flag to target Microsoft Entra ID as Identity Provider.");

        var tenantDomainOption = new Option<string?>(
            name: "--tenant-domain",
            description: "Microsoft Entra ID tenant domain (e.g. 'contoso.onmicrosoft.com'). Defaults to ENTRA_TENANT_DOMAIN or 'company.onmicrosoft.com'.");

        var cmd = new Command("ensure-groups", "Inspects rules.json and automatically provisions any missing managed security groups in the IdP.")
        {
            dryRunOption,
            jsonLogsOption,
            rulesOption,
            idpOption,
            entraOption,
            tenantDomainOption
        };

        cmd.SetHandler(async (InvocationContext context) =>
        {
            var dryRun = context.ParseResult.GetValueForOption(dryRunOption);
            var jsonLogs = context.ParseResult.GetValueForOption(jsonLogsOption);
            var rulesFile = context.ParseResult.GetValueForOption(rulesOption);
            var idpVal = context.ParseResult.GetValueForOption(idpOption);
            var entraFlag = context.ParseResult.GetValueForOption(entraOption);
            var tenantDomainVal = context.ParseResult.GetValueForOption(tenantDomainOption);

            var effectiveIdp = entraFlag ? "entra" : (idpVal?.Trim().ToLowerInvariant() ?? "in-memory");
            if (effectiveIdp is "entra-id" or "entraid" or "azure" or "azuread")
            {
                effectiveIdp = "entra";
            }

            var effectiveTenantDomain = !string.IsNullOrWhiteSpace(tenantDomainVal)
                ? tenantDomainVal.Trim()
                : (Environment.GetEnvironmentVariable("ENTRA_TENANT_DOMAIN") ?? "company.onmicrosoft.com");

            var cliOptions = new SyncCliOptions(
                RulesPath: rulesFile?.FullName,
                FixturesPath: null,
                Idp: effectiveIdp,
                TenantDomain: effectiveTenantDomain,
                SenderEmail: null,
                MockEmail: true
            );

            IServiceProvider sp;
            try
            {
                sp = serviceProviderFactory(cliOptions);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error initializing configuration: {ex.Message}");
                Console.ResetColor();
                context.ExitCode = 1;
                Environment.ExitCode = 1;
                return;
            }

            using var scope = sp.CreateScope();
            EnsureGroupsReport report;

            try
            {
                var useCase = scope.ServiceProvider.GetRequiredService<EnsureManagedGroupsUseCase>();
                report = await useCase.ExecuteAsync(dryRun, context.GetCancellationToken());
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"Ensure-groups execution failed: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.Error.WriteLine($"Details: {ex.InnerException.Message}");
                }
                Console.ResetColor();
                context.ExitCode = 1;
                Environment.ExitCode = 1;
                return;
            }

            if (jsonLogs)
            {
                Console.WriteLine(JsonSerializer.Serialize(report));
            }
            else
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("=================================================");
                Console.WriteLine($"  ENSURE MANAGED GROUPS REPORT (DryRun={dryRun}, IdP={effectiveIdp})");
                Console.WriteLine("=================================================");
                Console.ResetColor();
                Console.WriteLine($"Already Existing Groups:        {report.ExistingGroups.Count}");
                Console.WriteLine($"Missing Groups Identified:      {report.MissingGroups.Count}");
                Console.WriteLine($"Groups Created:                 {report.CreatedGroups.Count}");

                if (report.ExistingGroups.Count > 0)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine("\nExisting Managed Groups:");
                    foreach (var g in report.ExistingGroups)
                    {
                        Console.WriteLine($" = {g}");
                    }
                    Console.ResetColor();
                }

                if (report.MissingGroups.Count > 0)
                {
                    if (report.DryRun)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine("\nMissing Groups (To Be Created):");
                        foreach (var g in report.MissingGroups)
                        {
                            Console.WriteLine($" ? {g}");
                        }
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("\nSuccessfully Created Groups:");
                        foreach (var g in report.CreatedGroups)
                        {
                            Console.WriteLine($" + {g}");
                        }
                        Console.ResetColor();
                    }
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("\nAll declared rule groups are already present in the Identity Provider!");
                    Console.ResetColor();
                }

                Console.WriteLine();
            }
        });

        return cmd;
    }
}
