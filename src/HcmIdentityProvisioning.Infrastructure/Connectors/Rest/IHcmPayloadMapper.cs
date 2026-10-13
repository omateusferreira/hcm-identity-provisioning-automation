using HcmIdentityProvisioning.Domain.Common;
using HcmIdentityProvisioning.Domain.Entities;

namespace HcmIdentityProvisioning.Infrastructure.Connectors.Rest;

public interface IHcmPayloadMapper
{
    string BuildPageUri(string endpoint, int pageNumber, int pageSize);

    Task<PagedResult<Employee>> MapResponseAsync(
        HttpResponseMessage response,
        int pageNumber,
        int pageSize,
        CancellationToken ct = default);
}
