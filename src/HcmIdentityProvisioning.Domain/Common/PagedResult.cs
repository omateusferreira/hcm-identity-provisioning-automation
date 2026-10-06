namespace HcmIdentityProvisioning.Domain.Common;

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int PageNumber,
    int PageSize,
    int TotalCount,
    bool HasNextPage
);
