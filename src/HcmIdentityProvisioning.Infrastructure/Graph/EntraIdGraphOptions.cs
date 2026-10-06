namespace HcmIdentityProvisioning.Infrastructure.Graph;

using Azure.Core;

public sealed class EntraIdGraphOptions
{
    public string TenantDomain { get; set; } = string.Empty;
    public string ManagedGroupPrefix { get; set; } = "grp-iam-";
    public int MaxBatchSize { get; set; } = 20;
    public int FilterChunkSize { get; set; } = 15;
    public int MaxImmediateRetryDelaySeconds { get; set; } = 5;
    public TokenCredential? CustomCredential { get; set; }
}
