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
        if (TryGetDeliveryEmail(employee, out var email))
        {
            _logger.LogInformation("Credential dispatch mocked for employee {EmployeeId} ({Upn}) to out-of-band email {Email}", employee.Id, upn, email);
        }
        else
        {
            _logger.LogWarning("CREDENTIAL_DELIVERY_SKIPPED: Employee {EmployeeId} has no out-of-band email", employee.Id);
        }

        return Task.CompletedTask;
    }

    private static bool TryGetDeliveryEmail(Employee employee, out string email)
    {
        if (employee.ExtendedAttributes != null)
        {
            if (employee.ExtendedAttributes.TryGetValue("personalEmail", out var pe) && !string.IsNullOrWhiteSpace(pe))
            {
                email = pe;
                return true;
            }

            if (employee.ExtendedAttributes.TryGetValue("email", out var directEmail) && !string.IsNullOrWhiteSpace(directEmail))
            {
                email = directEmail;
                return true;
            }

            foreach (var kvp in employee.ExtendedAttributes)
            {
                if ((string.Equals(kvp.Key, "personalEmail", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(kvp.Key, "email", StringComparison.OrdinalIgnoreCase)) &&
                    !string.IsNullOrWhiteSpace(kvp.Value))
                {
                    email = kvp.Value;
                    return true;
                }
            }
        }

        email = string.Empty;
        return false;
    }
}
