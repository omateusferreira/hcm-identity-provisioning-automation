using System.Text.Json;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Ports;
using RulesEngine.Models;

namespace HcmIdentityProvisioning.Infrastructure.Rules;

public sealed class MicrosoftRulesEngineAdapter : IRulesEngine
{
    private readonly global::RulesEngine.RulesEngine _engine;
    private const string WorkflowName = "EntraIdGroupAssignment";

    public MicrosoftRulesEngineAdapter(Workflow[] workflows)
    {
        _engine = new global::RulesEngine.RulesEngine(workflows);
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
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Rules file not found at '{filePath}'.");

        var json = File.ReadAllText(filePath);
        return FromJsonString(json);
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
}
