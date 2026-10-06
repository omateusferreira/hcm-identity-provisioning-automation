using System.CommandLine;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Enums;
using HcmIdentityProvisioning.Domain.ValueObjects;
using HcmIdentityProvisioning.Infrastructure.Rules;

namespace HcmIdentityProvisioning.Cli.Commands;

public static class ValidateRulesCommand
{
    public static Command Create()
    {
        var rulesFileOption = new Option<FileInfo>(
            name: "--rules",
            description: "Path to rules.json file.",
            getDefaultValue: () => new FileInfo("src/HcmIdentityProvisioning.Infrastructure/Rules/rules.json"));

        var cmd = new Command("validate-rules", "Validates the syntax and evaluability of rules.json")
        {
            rulesFileOption
        };

        cmd.SetHandler(async (FileInfo file) =>
        {
            if (!file.Exists)
            {
                var resolved = ResolvePath(file.ToString());
                if (File.Exists(resolved))
                {
                    file = new FileInfo(resolved);
                }
            }

            if (!file.Exists)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: Rules file '{file.FullName}' not found.");
                Console.ResetColor();
                Environment.ExitCode = 1;
                return;
            }

            try
            {
                var adapter = MicrosoftRulesEngineAdapter.FromFile(file.FullName);
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

    private static string ResolvePath(string relativePath)
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
}
