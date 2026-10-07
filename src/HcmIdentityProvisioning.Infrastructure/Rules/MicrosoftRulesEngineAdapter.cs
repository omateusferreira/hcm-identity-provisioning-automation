using System.Text.Json;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Ports;
using RulesEngine.Models;

namespace HcmIdentityProvisioning.Infrastructure.Rules;

public sealed class MicrosoftRulesEngineAdapter : IRulesEngine
{
    private readonly global::RulesEngine.RulesEngine _engine;
    private readonly Workflow[] _workflows;
    private const string WorkflowName = "EntraIdGroupAssignment";

    public MicrosoftRulesEngineAdapter(Workflow[] workflows)
    {
        _workflows = workflows ?? Array.Empty<Workflow>();
        _engine = new global::RulesEngine.RulesEngine(_workflows);
    }

    public static MicrosoftRulesEngineAdapter FromJsonString(string json)
    {
        var workflows = JsonSerializer.Deserialize<Workflow[]>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? Array.Empty<Workflow>();

        return new MicrosoftRulesEngineAdapter(workflows);
    }

    public static MicrosoftRulesEngineAdapter FromFile(string filePath)
    {
        var resolvedPath = ResolvePath(filePath);
        if (!File.Exists(resolvedPath))
            throw new FileNotFoundException($"Rules file not found at '{filePath}'.");

        var json = File.ReadAllText(resolvedPath);
        return FromJsonString(json);
    }

    private static string ResolvePath(string path)
    {
        if (File.Exists(path))
            return path;

        var baseCandidate = Path.Combine(AppContext.BaseDirectory, path);
        if (File.Exists(baseCandidate))
            return baseCandidate;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var testPath = Path.Combine(dir.FullName, path);
            if (File.Exists(testPath))
                return testPath;

            dir = dir.Parent;
        }

        return path;
    }

    public async Task<IReadOnlySet<string>> EvaluateDesiredGroupsAsync(Employee employee, CancellationToken ct = default)
    {
        var ruleParams = new RuleParameter[]
        {
            new("Department", employee.Department),
            new("JobTitle", employee.JobTitle),
            new("Status", employee.Status),
            new("FullName", employee.FullName),
            new("ExtendedAttributes", employee.ExtendedAttributes)
        };

        var results = await _engine.ExecuteAllRulesAsync(WorkflowName, ruleParams);
        var matchedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var result in results)
        {
            if (result.IsSuccess && !string.IsNullOrWhiteSpace(result.Rule.SuccessEvent))
            {
                matchedGroups.Add(result.Rule.SuccessEvent);
            }
        }

        return matchedGroups;
    }

    public IReadOnlySet<string> GetDeclaredGroupNames()
    {
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var workflow in _workflows)
        {
            if (workflow.Rules == null) continue;
            foreach (var rule in workflow.Rules)
            {
                if (!string.IsNullOrWhiteSpace(rule.SuccessEvent))
                {
                    groups.Add(rule.SuccessEvent.Trim());
                }
            }
        }
        return groups;
    }
}
