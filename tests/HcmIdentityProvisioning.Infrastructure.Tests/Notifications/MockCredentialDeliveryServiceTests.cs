using FluentAssertions;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Enums;
using HcmIdentityProvisioning.Domain.ValueObjects;
using HcmIdentityProvisioning.Infrastructure.Notifications;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace HcmIdentityProvisioning.Infrastructure.Tests.Notifications;

public class MockCredentialDeliveryServiceTests
{
    private readonly ILogger<MockCredentialDeliveryService> _logger = Substitute.For<ILogger<MockCredentialDeliveryService>>();
    private readonly MockCredentialDeliveryService _service;

    public MockCredentialDeliveryServiceTests()
    {
        _service = new MockCredentialDeliveryService(_logger);
    }

    [Fact]
    public async Task DeliverInitialCredentialsAsync_WhenEmployeeHasEmail_LogsInformation()
    {
        // Arrange
        var empId = EmployeeId.Create("EMP001").Value;
        var upn = UserPrincipalName.Create("jane.doe@contoso.com").Value;
        var employee = new Employee(
            Id: empId,
            FullName: "Jane Doe",
            Status: EmployeeStatus.Active,
            Department: "Engineering",
            JobTitle: "Software Engineer",
            ExtendedAttributes: new Dictionary<string, string>
            {
                ["email"] = "jane.personal@example.com"
            }
        );

        // Act
        await _service.DeliverInitialCredentialsAsync(employee, upn, "TempPass123!");

        // Assert
        _logger.Received(1).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Credential dispatch mocked") &&
                                o.ToString()!.Contains("EMP001") &&
                                o.ToString()!.Contains("jane.personal@example.com")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>()
        );
    }

    [Fact]
    public async Task DeliverInitialCredentialsAsync_WhenEmployeeHasNoEmail_LogsWarning()
    {
        // Arrange
        var empId = EmployeeId.Create("EMP002").Value;
        var upn = UserPrincipalName.Create("john.doe@contoso.com").Value;
        var employee = new Employee(
            Id: empId,
            FullName: "John Doe",
            Status: EmployeeStatus.Active,
            Department: "Finance",
            JobTitle: "Financial Analyst",
            ExtendedAttributes: new Dictionary<string, string>()
        );

        // Act
        await _service.DeliverInitialCredentialsAsync(employee, upn, "TempPass123!");

        // Assert
        _logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("CREDENTIAL_DELIVERY_SKIPPED") &&
                                o.ToString()!.Contains("EMP002")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>()
        );
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DeliverInitialCredentialsAsync_WhenEmployeeEmailIsWhitespace_LogsWarning(string emptyEmail)
    {
        // Arrange
        var empId = EmployeeId.Create("EMP003").Value;
        var upn = UserPrincipalName.Create("bob.smith@contoso.com").Value;
        var employee = new Employee(
            Id: empId,
            FullName: "Bob Smith",
            Status: EmployeeStatus.Active,
            Department: "HR",
            JobTitle: "Recruiter",
            ExtendedAttributes: new Dictionary<string, string>
            {
                ["email"] = emptyEmail
            }
        );

        // Act
        await _service.DeliverInitialCredentialsAsync(employee, upn, "TempPass123!");

        // Assert
        _logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("CREDENTIAL_DELIVERY_SKIPPED") &&
                                o.ToString()!.Contains("EMP003")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>()
        );
    }

    [Theory]
    [InlineData("personalEmail")]
    [InlineData("PersonalEmail")]
    [InlineData("PERSONALEMAIL")]
    public async Task DeliverInitialCredentialsAsync_WhenEmployeeHasPersonalEmailKey_LogsInformation(string keyName)
    {
        // Arrange
        var empId = EmployeeId.Create("EMP004").Value;
        var upn = UserPrincipalName.Create("lucas.moura@contoso.com").Value;
        var employee = new Employee(
            Id: empId,
            FullName: "Lucas Moura",
            Status: EmployeeStatus.Active,
            Department: "Sales",
            JobTitle: "Account Exec",
            ExtendedAttributes: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [keyName] = "lucas.personal@example.com"
            }
        );

        // Act
        await _service.DeliverInitialCredentialsAsync(employee, upn, "TempPass123!");

        // Assert
        _logger.Received(1).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Credential dispatch mocked") &&
                                o.ToString()!.Contains("EMP004") &&
                                o.ToString()!.Contains("lucas.personal@example.com")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>()
        );
    }
}
