using FluentAssertions;
using HcmIdentityProvisioning.Domain.Enums;
using HcmIdentityProvisioning.Infrastructure.Connectors.Synthetic;
using Xunit;

namespace HcmIdentityProvisioning.Infrastructure.Tests.Connectors;

public class SyntheticHcmConnectorTests
{
    [Fact]
    public async Task GetEmployeesPageAsync_ReturnsPaginatedResults()
    {
        var connector = SyntheticHcmConnector.FromFixturesFile("fixtures/synthetic-employees.json");
        var page = await connector.GetEmployeesPageAsync(1, 2);

        page.Items.Should().HaveCount(2);
        page.TotalCount.Should().Be(6);
        page.HasNextPage.Should().BeTrue();
    }

    [Fact]
    public async Task FromFixturesFile_LoadsAllCanonicalEmployeesCorrectly()
    {
        var connector = SyntheticHcmConnector.FromFixturesFile("fixtures/synthetic-employees.json");
        var page = await connector.GetEmployeesPageAsync(1, 10);

        page.Items.Should().HaveCount(6);
        page.TotalCount.Should().Be(6);
        page.HasNextPage.Should().BeFalse();

        var emp1 = page.Items.First(e => e.Id.Value == "EMP-001");
        emp1.FullName.Should().Be("Mariana Lima");
        emp1.Status.Should().Be(EmployeeStatus.Active);
        emp1.Department.Should().Be("Tecnologia");
        emp1.JobTitle.Should().Be("Engenheiro de Software");
        emp1.ExtendedAttributes.Should().ContainKey("email").WhoseValue.Should().Be("mariana.personal@example.com");

        var emp3 = page.Items.First(e => e.Id.Value == "EMP-003");
        emp3.FullName.Should().Be("Beatriz Souza");
        emp3.Status.Should().Be(EmployeeStatus.Inactive);

        var emp5 = page.Items.First(e => e.Id.Value == "EMP-005");
        emp5.FullName.Should().Be("José d'Ávila");
    }

    [Fact]
    public async Task GetEmployeesPageAsync_LastPage_HasNextPageIsFalse()
    {
        var connector = SyntheticHcmConnector.FromFixturesFile("fixtures/synthetic-employees.json");

        var page3 = await connector.GetEmployeesPageAsync(3, 2);
        page3.Items.Should().HaveCount(2);
        page3.HasNextPage.Should().BeFalse();

        var page4 = await connector.GetEmployeesPageAsync(4, 2);
        page4.Items.Should().BeEmpty();
        page4.HasNextPage.Should().BeFalse();
    }

    [Fact]
    public void FromFixturesFile_WhenFileNotFound_ThrowsFileNotFoundException()
    {
        var act = () => SyntheticHcmConnector.FromFixturesFile("fixtures/non-existent-fixtures.json");

        act.Should().Throw<FileNotFoundException>()
            .WithMessage("*non-existent-fixtures.json*");
    }

    [Fact]
    public async Task FromJson_ParsesValidJsonCorrectly()
    {
        var json = """
        [
          {
            "id": "EMP-999",
            "fullName": "Custom Tester",
            "status": "Active",
            "department": "QA",
            "jobTitle": "Tester",
            "extendedAttributes": { "role": "lead" }
          }
        ]
        """;

        var connector = SyntheticHcmConnector.FromJson(json);
        var page = await connector.GetEmployeesPageAsync(1, 10);

        page.Items.Should().HaveCount(1);
        var emp = page.Items[0];
        emp.Id.Value.Should().Be("EMP-999");
        emp.FullName.Should().Be("Custom Tester");
        emp.ExtendedAttributes.Should().ContainKey("role").WhoseValue.Should().Be("lead");
    }
}
