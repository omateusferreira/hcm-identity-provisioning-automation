using FluentAssertions;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Enums;
using HcmIdentityProvisioning.Domain.ValueObjects;
using HcmIdentityProvisioning.Infrastructure.Rules;
using Xunit;

namespace HcmIdentityProvisioning.Infrastructure.Tests.Rules;

public class MicrosoftRulesEngineAdapterTests
{
    private const string SampleRulesJson = @"[
      {
        ""WorkflowName"": ""EntraIdGroupAssignment"",
        ""Rules"": [
          {
            ""RuleName"": ""FinanceGroup"",
            ""SuccessEvent"": ""grp-iam-finance"",
            ""Expression"": ""Department == \""Financeiro\""""
          },
          {
            ""RuleName"": ""TechEngineer"",
            ""SuccessEvent"": ""grp-iam-engineering"",
            ""Expression"": ""Department == \""Tecnologia\"" AND JobTitle.Contains(\""Engenheiro\"")""
          }
        ]
      }
    ]";

    [Fact]
    public async Task EvaluateDesiredGroupsAsync_WhenMatchesDepartmentAndRole_ReturnsGroups()
    {
        var adapter = MicrosoftRulesEngineAdapter.FromJsonString(SampleRulesJson);
        var emp = new Employee(
            EmployeeId.Create("101").Value,
            "Lucas Engenheiro",
            EmployeeStatus.Active,
            "Tecnologia",
            "Engenheiro de Software Sênior",
            new Dictionary<string, string>()
        );

        var groups = await adapter.EvaluateDesiredGroupsAsync(emp);

        groups.Should().Contain("grp-iam-engineering");
        groups.Should().NotContain("grp-iam-finance");
    }

    [Fact]
    public async Task EvaluateDesiredGroupsAsync_WhenNoRulesMatch_ReturnsEmptySet()
    {
        var adapter = MicrosoftRulesEngineAdapter.FromJsonString(SampleRulesJson);
        var emp = new Employee(
            EmployeeId.Create("102").Value,
            "Mariana Recursos Humanos",
            EmployeeStatus.Active,
            "RH",
            "Analista",
            new Dictionary<string, string>()
        );

        var groups = await adapter.EvaluateDesiredGroupsAsync(emp);

        groups.Should().BeEmpty();
    }

    [Fact]
    public void FromFile_WhenFileDoesNotExist_ThrowsFileNotFoundException()
    {
        var action = () => MicrosoftRulesEngineAdapter.FromFile("non-existent-rules.json");

        action.Should().Throw<FileNotFoundException>()
            .WithMessage("*Rules file not found at 'non-existent-rules.json'.*");
    }

    [Fact]
    public async Task FromFile_WithDefaultRulesFile_MatchesMultipleRules()
    {
        // Locate rules.json from source or output directory
        var rulesPath = Path.Combine(AppContext.BaseDirectory, "Rules", "rules.json");
        if (!File.Exists(rulesPath))
        {
            rulesPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "HcmIdentityProvisioning.Infrastructure", "Rules", "rules.json"));
        }

        var adapter = MicrosoftRulesEngineAdapter.FromFile(rulesPath);
        var emp = new Employee(
            EmployeeId.Create("103").Value,
            "Engenheiro Ativo",
            EmployeeStatus.Active,
            "Tecnologia",
            "Engenheiro de Dados",
            new Dictionary<string, string>()
        );

        var groups = await adapter.EvaluateDesiredGroupsAsync(emp);

        // Tech engineer matches both EngineeringAccess and AllStaffBaseAccess
        groups.Should().Contain("grp-iam-engineering");
        groups.Should().Contain("grp-iam-all-staff");
        groups.Should().NotContain("grp-iam-finance");
    }
}
