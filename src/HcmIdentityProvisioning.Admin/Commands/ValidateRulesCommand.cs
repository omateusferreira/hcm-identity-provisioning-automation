using System.CommandLine;
using HcmIdentityProvisioning.Admin.Utils;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Enums;
using HcmIdentityProvisioning.Domain.ValueObjects;
using HcmIdentityProvisioning.Infrastructure.Rules;

namespace HcmIdentityProvisioning.Admin.Commands;

public static class ValidateRulesCommand
{
    public static Command Create()
    {
        var rulesFileOption = new Option<FileInfo?>(
            name: "--rules",
            description: "Path to rules.json file (defaults to application bundle or RULES_FILE_PATH).");

        var cmd = new Command("validate-rules", "Validates the syntax and evaluability of rules.json")
        {
            rulesFileOption
        };

        cmd.SetHandler(async (FileInfo? file) =>
        {
            string resolvedPath;
            try
            {
                resolvedPath = PathResolver.ResolveRulesPath(file?.FullName);
            }
            catch (FileNotFoundException ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: {ex.Message}");
                Console.ResetColor();
                Environment.ExitCode = 1;
                return;
            }

            try
            {
                var adapter = MicrosoftRulesEngineAdapter.FromFile(resolvedPath);
                var probeEmployee = new Employee(
                    EmployeeId.Create("TEST-01").Value,
                    "Probe Employee",
                    EmployeeStatus.Active,
                    "Tecnologia",
                    "Engenheiro",
                    new Dictionary<string, string>()
                );

                var groups = await adapter.EvaluateDesiredGroupsAsync(probeEmployee);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"SUCCESS: Rules syntax valid! Evaluated sample probe employee into {groups.Count} group(s): [{string.Join(", ", groups)}]");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"FAILURE: Rules evaluation failed: {ex.Message}");
                Console.ResetColor();
                Environment.ExitCode = 1;
            }
        }, rulesFileOption);

        return cmd;
    }
}
