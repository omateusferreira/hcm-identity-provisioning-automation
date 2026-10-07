namespace HcmIdentityProvisioning.Cli.Commands;

public sealed record SyncCliOptions(
    string? RulesPath = null,
    string? FixturesPath = null,
    string Idp = "in-memory",
    string TenantDomain = "company.onmicrosoft.com",
    string? SenderEmail = null,
    bool MockEmail = false
);
