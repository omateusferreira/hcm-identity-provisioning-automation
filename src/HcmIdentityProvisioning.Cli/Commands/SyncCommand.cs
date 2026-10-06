using System.CommandLine;
using System.Text.Json;
using HcmIdentityProvisioning.Application.Models;
using HcmIdentityProvisioning.Application.UseCases;
using Microsoft.Extensions.DependencyInjection;

namespace HcmIdentityProvisioning.Cli.Commands;

public static class SyncCommand
{
    public static Command Create(IServiceProvider serviceProvider) =>
        Create((_, _) => serviceProvider);

    public static Command Create(Func<string?, string?, IServiceProvider> serviceProviderFactory)
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

        var cmd = new Command("sync", "Executes HCM to Entra ID identity lifecycle synchronization.")
        {
            dryRunOption,
            jsonLogsOption,
            rulesOption,
            fixturesOption
        };

        cmd.SetHandler(async (bool dryRun, bool jsonLogs, FileInfo? rulesFile, FileInfo? fixturesFile) =>
        {
            IServiceProvider sp;
            try
            {
                sp = serviceProviderFactory(rulesFile?.FullName, fixturesFile?.FullName);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error initializing configuration: {ex.Message}");
                Console.ResetColor();
                Environment.ExitCode = 1;
                return;
            }

            using var scope = sp.CreateScope();
            SyncReport report;

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

            if (report.CircuitBreakerTripped)
            {
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
                Console.WriteLine($"  HCM IDENTITY RECONCILIATION REPORT (DryRun={dryRun})");
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
        }, dryRunOption, jsonLogsOption, rulesOption, fixturesOption);

        return cmd;
    }
}
