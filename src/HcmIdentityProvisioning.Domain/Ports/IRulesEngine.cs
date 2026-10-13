using HcmIdentityProvisioning.Domain.Entities;

namespace HcmIdentityProvisioning.Domain.Ports;

public interface IRulesEngine
{
    Task<IReadOnlySet<string>> EvaluateDesiredGroupsAsync(
        Employee employee,
        CancellationToken ct = default);

    IReadOnlySet<string> GetDeclaredGroupNames();
}
