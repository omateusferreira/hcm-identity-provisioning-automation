using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace HcmIdentityProvisioning.Infrastructure.Notifications;

public sealed class MockCredentialDeliveryService : ICredentialDeliveryService
{
    private readonly ILogger<MockCredentialDeliveryService> _logger;

    public MockCredentialDeliveryService(ILogger<MockCredentialDeliveryService> logger)
    {
        _logger = logger;
    }

    public Task DeliverInitialCredentialsAsync(
        Employee employee,
        UserPrincipalName upn,
        string temporaryPassword,
        CancellationToken ct = default)
    {
        if (employee.ExtendedAttributes.TryGetValue("email", out var email) && !string.IsNullOrWhiteSpace(email))
        {
            _logger.LogInformation("Credential dispatch mocked for employee {EmployeeId} ({Upn}) to out-of-band email {Email}", employee.Id, upn, email);
        }
        else
        {
            _logger.LogWarning("CREDENTIAL_DELIVERY_SKIPPED: Employee {EmployeeId} has no out-of-band email", employee.Id);
        }

        return Task.CompletedTask;
    }
}
