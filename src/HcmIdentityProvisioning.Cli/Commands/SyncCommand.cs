using System.CommandLine;
using System.Text.Json;
using HcmIdentityProvisioning.Application.Models;
using HcmIdentityProvisioning.Application.UseCases;
using Microsoft.Extensions.DependencyInjection;

namespace HcmIdentityProvisioning.Cli.Commands;

public static class SyncCommand
{
    public static Command Create(IServiceProvider serviceProvider) =>
        Create(_ => serviceProvider);

    public static Command Create(Func<string?, string?, IServiceProvider> serviceProviderFactory) =>
        Create(opts => serviceProviderFactory(opts.RulesPath, opts.FixturesPath));

    public static Command Create(Func<SyncCliOptions, IServiceProvider> serviceProviderFactory)
    {
        var dryRunOption = new Option<bool>(
            name: "--dry-run",
            description: "Execute reconciliation in audit mode without committing changes.");

        var jsonLogsOption = new Option<bool>(
            name: "--json-logs",
            description: "Output audit report as structured NDJSON for SIEM ingestion.");

        var rulesOption = new Option<FileInfo?>(
            name: "--rules",
            description: "Path to rules.json file (defaults to application bundle or RULES_FILE_PATH).");

        var fixturesOption = new Option<FileInfo?>(
            name: "--fixtures",
            description: "Path to synthetic-employees.json file (defaults to application bundle or FIXTURES_FILE_PATH).");

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

        var senderEmailOption = new Option<string?>(
            name: "--sender-email",
            description: "Shared mailbox sender email for Graph credential delivery (e.g. 'no-reply@contoso.onmicrosoft.com').");

        var mockEmailOption = new Option<bool>(
            name: "--mock-email",
            description: "Simulate credential delivery in logs instead of sending real emails via Microsoft Graph.");

        var cmd = new Command("sync", "Executes HCM to Entra ID identity lifecycle synchronization.")
        {
            dryRunOption,
            jsonLogsOption,
            rulesOption,
            fixturesOption,
            idpOption,
            entraOption,
            tenantDomainOption,
            senderEmailOption,
            mockEmailOption
        };

        cmd.SetHandler(async (System.CommandLine.Invocation.InvocationContext context) =>
        {
            var dryRun = context.ParseResult.GetValueForOption(dryRunOption);
            var jsonLogs = context.ParseResult.GetValueForOption(jsonLogsOption);
            var rulesFile = context.ParseResult.GetValueForOption(rulesOption);
            var fixturesFile = context.ParseResult.GetValueForOption(fixturesOption);
            var idpVal = context.ParseResult.GetValueForOption(idpOption);
            var entraFlag = context.ParseResult.GetValueForOption(entraOption);
            var tenantDomainVal = context.ParseResult.GetValueForOption(tenantDomainOption);
            var senderEmailVal = context.ParseResult.GetValueForOption(senderEmailOption);
            var mockEmailFlag = context.ParseResult.GetValueForOption(mockEmailOption);

            var effectiveIdp = entraFlag ? "entra" : (idpVal?.Trim().ToLowerInvariant() ?? "in-memory");
            if (effectiveIdp is "entra-id" or "entraid" or "azure" or "azuread")
            {
                effectiveIdp = "entra";
            }

            var effectiveTenantDomain = !string.IsNullOrWhiteSpace(tenantDomainVal)
                ? tenantDomainVal.Trim()
                : (Environment.GetEnvironmentVariable("ENTRA_TENANT_DOMAIN") ?? "company.onmicrosoft.com");

            var effectiveSenderEmail = !string.IsNullOrWhiteSpace(senderEmailVal)
                ? senderEmailVal.Trim()
                : Environment.GetEnvironmentVariable("GRAPH_SENDER_EMAIL");

            var cliOptions = new SyncCliOptions(
                rulesFile?.FullName,
                fixturesFile?.FullName,
                effectiveIdp,
                effectiveTenantDomain,
                effectiveSenderEmail,
                mockEmailFlag
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
            SyncReport report;

            try
            {
                if (dryRun)
                {
                    var useCase = scope.ServiceProvider.GetRequiredService<DryRunAuditUseCase>();
                    report = await useCase.ExecuteAsync();
                }
                else
                {
                    var useCase = scope.ServiceProvider.GetRequiredService<ReconcileBatchUseCase>();
                    report = await useCase.ExecuteAsync();
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"Sync execution failed: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.Error.WriteLine($"Details: {ex.InnerException.Message}");
                }
                Console.ResetColor();
                context.ExitCode = 1;
                Environment.ExitCode = 1;
                return;
            }

            if (report.CircuitBreakerTripped)
            {
                context.ExitCode = 2;
                Environment.ExitCode = 2;
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
                Console.WriteLine($"  HCM IDENTITY RECONCILIATION REPORT (DryRun={dryRun}, IdP={effectiveIdp})");
                Console.WriteLine("=================================================");
                Console.ResetColor();
                Console.WriteLine($"Total Employees Processed:      {report.TotalProcessed}");
                Console.WriteLine($"Users Created (Joiners):        {report.CreatedCount}");
                Console.WriteLine($"Profiles Updated:               {report.UpdatedCount}");
                Console.WriteLine($"Accounts Enabled:               {report.EnabledCount}");
                Console.WriteLine($"Accounts Disabled (Leavers):    {report.DisabledCount}");
                Console.WriteLine($"Sessions Revoked:               {report.SessionsRevokedCount}");
                Console.WriteLine($"Group Memberships Added:        {report.GroupMembershipsAdded}");
                Console.WriteLine($"Group Memberships Removed:      {report.GroupMembershipsRemoved}");

                if (report.CircuitBreakerTripped)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"CIRCUIT BREAKER: TRIPPED! -> {report.CircuitBreakerMessage}");
                    Console.ResetColor();
                }

                if (report.Warnings.Count > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("\nWarnings:");
                    foreach (var w in report.Warnings)
                    {
                        Console.WriteLine($" - {w}");
                    }
                    Console.ResetColor();
                }
                Console.WriteLine();
            }
        });

        return cmd;
    }
}
