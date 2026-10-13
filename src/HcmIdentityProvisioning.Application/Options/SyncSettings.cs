namespace HcmIdentityProvisioning.Application.Options;

public sealed class SyncSettings
{
    public string TenantDomain { get; set; } = "company.onmicrosoft.com";
    public string ManagedGroupPrefix { get; set; } = "grp-iam-";
    public int BatchPageSize { get; set; } = 50;
    public double MaxDisablementPercentage { get; set; } = 10.0;
    public int MaxDisablementCount { get; set; } = 25;
    public bool HaltAllOperationsOnTrip { get; set; } = false;
}
