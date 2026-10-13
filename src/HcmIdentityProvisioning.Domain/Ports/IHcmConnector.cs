using HcmIdentityProvisioning.Domain.Common;
using HcmIdentityProvisioning.Domain.Entities;

namespace HcmIdentityProvisioning.Domain.Ports;

public interface IHcmConnector
{
    Task<PagedResult<Employee>> GetEmployeesPageAsync(
        int pageNumber,
        int pageSize,
        CancellationToken ct = default);
}
