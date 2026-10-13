using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.ValueObjects;

namespace HcmIdentityProvisioning.Domain.Ports;

public interface ICredentialDeliveryService
{
    Task DeliverInitialCredentialsAsync(
        Employee employee,
        UserPrincipalName upn,
        string temporaryPassword,
        CancellationToken ct = default);
}
