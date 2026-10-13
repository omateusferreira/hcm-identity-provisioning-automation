using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Text.Json;
using HcmIdentityProvisioning.Admin.Extensibility;
using HcmIdentityProvisioning.Admin.Utils;
using HcmIdentityProvisioning.Application.Models;
using HcmIdentityProvisioning.Application.UseCases;
using Microsoft.Extensions.DependencyInjection;

namespace HcmIdentityProvisioning.Admin.Commands;

public static class SyncCommand
{
    public static Command Create(
        IServiceProvider serviceProvider,
        IConsolePrompter? prompter = null,
        IReadOnlyList<IHcmConnectorCliDescriptor>? descriptors = null) =>
        Create((SyncCliOptions _, ParseResult _) => serviceProvider, prompter, descriptors);

    public static Command Create(
        Func<string?, string?, IServiceProvider> serviceProviderFactory,
        IConsolePrompter? prompter = null,
        IReadOnlyList<IHcmConnectorCliDescriptor>? descriptors = null) =>
        Create((SyncCliOptions opts, ParseResult _) => serviceProviderFactory(opts.RulesPath, opts.FixturesPath), prompter, descriptors);

    public static Command Create(
        Func<SyncCliOptions, IServiceProvider> serviceProviderFactory,
        IConsolePrompter? prompter = null,
        IReadOnlyList<IHcmConnectorCliDescriptor>? descriptors = null) =>
        Create((SyncCliOptions opts, ParseResult _) => serviceProviderFactory(opts), prompter, descriptors);

    public static Command Create(
        Func<SyncCliOptions, ParseResult, IServiceProvider> serviceProviderFactory,
        IConsolePrompter? prompter = null,
        IReadOnlyList<IHcmConnectorCliDescriptor>? descriptors = null)
    {
        var effectivePrompter = prompter ?? new ConsolePrompter();

        var dryRunOption = new Option<bool>(
            name: "--dry-run",
            description: "Execute reconciliation in audit mode without committing changes.");

        var yesOption = new Option<bool>(
            aliases: ["--yes", "-y"],
            description: "Automatic yes to prompts; run non-interactively without prompting for confirmation.");

        var jsonLogsOption = new Option<bool>(
            name: "--json-logs",
            description: "Output audit report as structured NDJSON for SIEM ingestion.");

        var rulesOption = new Option<FileInfo?>(
            name: "--rules",
            description: "Path to rules.json file (defaults to application bundle or RULES_FILE_PATH).");

        var fixturesOption = descriptors?
            .SelectMany(d => d.GetCliOptions())
            .OfType<Option<FileInfo?>>()
            .FirstOrDefault(o => o.Aliases.Contains("--fixtures") || o.Name == "fixtures")
            ?? new Option<FileInfo?>(
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
            yesOption,
            jsonLogsOption,
            rulesOption,
            fixturesOption,
            idpOption,
            entraOption,
            tenantDomainOption,
            senderEmailOption,
            mockEmailOption
        };

        if (descriptors != null)
        {
            foreach (var descriptor in descriptors)
            {
                foreach (var opt in descriptor.GetCliOptions())
                {
                    if (opt != fixturesOption && !cmd.Options.Any(o => o.Name == opt.Name || o.Aliases.Any(a => opt.Aliases.Contains(a))))
                    {
                        cmd.AddOption(opt);
                    }
                }
            }
        }

        cmd.SetHandler(async (InvocationContext context) =>
        {
            var dryRun = context.ParseResult.GetValueForOption(dryRunOption);
            var yesFlag = context.ParseResult.GetValueForOption(yesOption);
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
                sp = serviceProviderFactory(cliOptions, context.ParseResult);
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
                    report = await useCase.ExecuteAsync(context.GetCancellationToken());
                }
                else
                {
                    var auditUseCase = scope.ServiceProvider.GetRequiredService<DryRunAuditUseCase>();
                    var plannedReport = await auditUseCase.ExecuteAsync(context.GetCancellationToken());

                    var totalPlannedMutations = plannedReport.CreatedCount
                        + plannedReport.UpdatedCount
                        + plannedReport.EnabledCount
                        + plannedReport.DisabledCount
                        + plannedReport.SessionsRevokedCount
                        + plannedReport.GroupMembershipsAdded
                        + plannedReport.GroupMembershipsRemoved;

                    if (totalPlannedMutations > 0 && !yesFlag)
                    {
                        Console.WriteLine();
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"[CONFIRMATION REQUIRED] The following actions will be executed against the Identity Provider ({effectiveIdp}):");
                        Console.WriteLine($"  - Users to create:            {plannedReport.CreatedCount}");
                        Console.WriteLine($"  - Profiles to update:          {plannedReport.UpdatedCount}");
                        Console.WriteLine($"  - Accounts to enable:          {plannedReport.EnabledCount}");
                        Console.WriteLine($"  - Accounts to disable:         {plannedReport.DisabledCount}");
                        Console.WriteLine($"  - Sessions to revoke:          {plannedReport.SessionsRevokedCount}");
                        Console.WriteLine($"  - Group memberships to add:    {plannedReport.GroupMembershipsAdded}");
                        Console.WriteLine($"  - Group memberships to remove: {plannedReport.GroupMembershipsRemoved}");

                        if (plannedReport.CircuitBreakerTripped)
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"\n[CIRCUIT BREAKER WARNING] {plannedReport.CircuitBreakerMessage}");
                        }

                        Console.ResetColor();

                        if (effectivePrompter.IsInputRedirected)
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine("\n[GUARD] Non-interactive environment detected. Use '--yes' / '-y' to confirm execution.");
                            Console.ResetColor();
                            context.ExitCode = 1;
                            Environment.ExitCode = 1;
                            return;
                        }

                        if (!effectivePrompter.Confirm("\nDo you want to apply these changes?"))
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine("\n[CANCELLED] Operation cancelled by user. No changes were made.");
                            Console.ResetColor();
                            context.ExitCode = 0;
                            Environment.ExitCode = 0;
                            return;
                        }
                    }

                    var useCase = scope.ServiceProvider.GetRequiredService<ReconcileBatchUseCase>();
                    report = await useCase.ExecuteAsync(context.GetCancellationToken());
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
