namespace HcmIdentityProvisioning.Application.Models;

public sealed record EnsureGroupsReport(
    IReadOnlyList<string> ExistingGroups,
    IReadOnlyList<string> MissingGroups,
    IReadOnlyList<string> CreatedGroups,
    bool DryRun
);
