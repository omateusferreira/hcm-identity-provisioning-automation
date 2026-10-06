namespace HcmIdentityProvisioning.Infrastructure.Graph;

using Azure.Core;
using Azure.Identity;
using HcmIdentityProvisioning.Domain.Actions;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Graph;

public class EntraIdGraphAdapter : IIdentityStore
{
    private readonly EntraIdGraphOptions _options;
    private readonly ILogger<EntraIdGraphAdapter> _logger;
    private readonly GraphServiceClient _graphClient;

    public EntraIdGraphAdapter(
        IOptions<EntraIdGraphOptions> options,
        ILogger<EntraIdGraphAdapter> logger,
        GraphServiceClient? graphClient = null,
        HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _logger = logger;

        if (graphClient != null)
        {
            _graphClient = graphClient;
        }
        else if (httpClient != null)
        {
            var credential = _options.CustomCredential ?? new DefaultAzureCredential();
            _graphClient = new GraphServiceClient(httpClient, credential);
        }
        else
        {
            var credential = _options.CustomCredential ?? new DefaultAzureCredential();
            _graphClient = new GraphServiceClient(credential);
        }
    }

    public EntraIdGraphAdapter(
        IOptions<EntraIdGraphOptions> options,
        ILogger<EntraIdGraphAdapter> logger,
        HttpClient httpClient)
        : this(options, logger, null, httpClient)
    {
    }

    public EntraIdGraphAdapter(
        IOptions<EntraIdGraphOptions> options,
        ILogger<EntraIdGraphAdapter> logger,
        GraphServiceClient graphClient)
        : this(options, logger, graphClient, null)
    {
    }

    public EntraIdGraphAdapter(
        IOptions<EntraIdGraphOptions> options,
        ILogger<EntraIdGraphAdapter> logger)
        : this(options, logger, null, null)
    {
    }

    public async Task<IReadOnlyDictionary<string, ManagedGroup>> GetManagedGroupsAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("Retrieving managed groups starting with prefix '{Prefix}'", _options.ManagedGroupPrefix);

        var groupsByName = new Dictionary<string, ManagedGroup>(StringComparer.OrdinalIgnoreCase);

        var response = await _graphClient.Groups.GetAsync(rc =>
        {
            rc.QueryParameters.Filter = $"startswith(displayName, '{_options.ManagedGroupPrefix}')";
            rc.QueryParameters.Select = ["id", "displayName"];
        }, ct);

        while (response != null)
        {
            if (response.Value != null)
            {
                foreach (var group in response.Value)
                {
                    if (group.Id != null &&
                        Guid.TryParse(group.Id, out var groupId) &&
                        !string.IsNullOrWhiteSpace(group.DisplayName) &&
                        group.DisplayName.StartsWith(_options.ManagedGroupPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        groupsByName[group.DisplayName] = new ManagedGroup(groupId, group.DisplayName);
                    }
                }
            }

            if (string.IsNullOrEmpty(response.OdataNextLink))
            {
                break;
            }

            response = await _graphClient.Groups.WithUrl(response.OdataNextLink).GetAsync(cancellationToken: ct);
        }

        _logger.LogInformation("Found {Count} managed groups matching prefix '{Prefix}'", groupsByName.Count, _options.ManagedGroupPrefix);
        return groupsByName;
    }

    public async Task<bool> IsUserPrincipalNameAvailableAsync(UserPrincipalName upn, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(upn);

        _logger.LogDebug("Checking availability for UPN '{Upn}'", upn.Value);

        var response = await _graphClient.Users.GetAsync(rc =>
        {
            rc.QueryParameters.Filter = $"userPrincipalName eq '{upn.Value}'";
            rc.QueryParameters.Select = ["id"];
        }, ct);

        var count = response?.Value?.Count ?? 0;
        var isAvailable = count == 0;

        _logger.LogDebug("UPN '{Upn}' available: {IsAvailable}", upn.Value, isAvailable);
        return isAvailable;
    }

    public async Task<IReadOnlyDictionary<EmployeeId, EntraUser>> GetUsersByEmployeeIdsAsync(
        IEnumerable<EmployeeId> employeeIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(employeeIds);

        var distinctEmployeeIds = employeeIds.Distinct().ToList();
        if (distinctEmployeeIds.Count == 0)
        {
            return new Dictionary<EmployeeId, EntraUser>();
        }

        _logger.LogDebug("Querying Entra ID for {Count} distinct employee IDs", distinctEmployeeIds.Count);

        // Load managed groups to filter membership within the managed boundary
        var managedGroups = await GetManagedGroupsAsync(ct);
        var managedGroupIds = managedGroups.Values.Select(g => g.Id).ToHashSet();

        var chunkSize = _options.FilterChunkSize > 0 ? _options.FilterChunkSize : 15;
        var usersByEmployeeId = new Dictionary<EmployeeId, EntraUser>();

        foreach (var chunk in distinctEmployeeIds.Chunk(chunkSize))
        {
            var idList = string.Join("', '", chunk.Select(e => e.Value));
            var usersResponse = await _graphClient.Users.GetAsync(rc =>
            {
                rc.QueryParameters.Filter = $"employeeId in ('{idList}')";
                rc.QueryParameters.Select = ["id", "employeeId", "userPrincipalName", "displayName", "accountEnabled"];
            }, ct);

            while (usersResponse != null)
            {
                if (usersResponse.Value != null)
                {
                    foreach (var user in usersResponse.Value)
                    {
                        if (string.IsNullOrWhiteSpace(user.Id) || !Guid.TryParse(user.Id, out var graphId))
                        {
                            _logger.LogWarning("Skipping user with invalid Graph ID: {UserId}", user.Id);
                            continue;
                        }

                        var empIdResult = EmployeeId.Create(user.EmployeeId);
                        if (!empIdResult.IsSuccess)
                        {
                            _logger.LogWarning("Skipping user {UserId} with invalid employeeId: {EmpId}", user.Id, user.EmployeeId);
                            continue;
                        }

                        var upnResult = UserPrincipalName.Create(user.UserPrincipalName);
                        if (!upnResult.IsSuccess)
                        {
                            _logger.LogWarning("Skipping user {UserId} with invalid UPN: {Upn}", user.Id, user.UserPrincipalName);
                            continue;
                        }

                        // Resolve user groups and filter strictly to managed scope
                        var assignedGroupIds = await ResolveManagedGroupsForUserAsync(user.Id, managedGroupIds, ct);

                        var entraUser = new EntraUser(
                            graphId,
                            empIdResult.Value,
                            upnResult.Value,
                            user.DisplayName ?? string.Empty,
                            user.AccountEnabled ?? false,
                            assignedGroupIds
                        );

                        usersByEmployeeId[empIdResult.Value] = entraUser;
                    }
                }

                if (string.IsNullOrEmpty(usersResponse.OdataNextLink))
                {
                    break;
                }

                usersResponse = await _graphClient.Users.WithUrl(usersResponse.OdataNextLink).GetAsync(cancellationToken: ct);
            }
        }

        _logger.LogInformation("Resolved {Count} Entra users for {Total} queried employee IDs", usersByEmployeeId.Count, distinctEmployeeIds.Count);
        return usersByEmployeeId;
    }

    public Task ApplyBatchMutationsAsync(IEnumerable<DeltaAction> actions, CancellationToken ct = default)
    {
        throw new NotImplementedException("Batch mutations will be implemented in Task 5.");
    }

    private async Task<IReadOnlySet<Guid>> ResolveManagedGroupsForUserAsync(
        string userId,
        HashSet<Guid> managedGroupIds,
        CancellationToken ct)
    {
        var assignedManagedGroupIds = new HashSet<Guid>();

        var memberOfResponse = await _graphClient.Users[userId].MemberOf.GetAsync(rc =>
        {
            rc.QueryParameters.Select = ["id"];
        }, ct);

        while (memberOfResponse != null)
        {
            if (memberOfResponse.Value != null)
            {
                foreach (var dirObj in memberOfResponse.Value)
                {
                    if (dirObj.Id != null &&
                        Guid.TryParse(dirObj.Id, out var groupId) &&
                        managedGroupIds.Contains(groupId))
                    {
                        assignedManagedGroupIds.Add(groupId);
                    }
                }
            }

            if (string.IsNullOrEmpty(memberOfResponse.OdataNextLink))
            {
                break;
            }

            memberOfResponse = await _graphClient.Users[userId].MemberOf
                .WithUrl(memberOfResponse.OdataNextLink)
                .GetAsync(cancellationToken: ct);
        }

        return assignedManagedGroupIds;
    }
}
