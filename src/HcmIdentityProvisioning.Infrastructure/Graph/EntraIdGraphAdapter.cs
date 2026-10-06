namespace HcmIdentityProvisioning.Infrastructure.Graph;

using System.Net;
using Azure.Core;
using Azure.Identity;
using HcmIdentityProvisioning.Domain.Actions;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;

public class EntraIdGraphAdapter : IIdentityStore
{
    private readonly EntraIdGraphOptions _options;
    private readonly ILogger<EntraIdGraphAdapter> _logger;
    private readonly GraphServiceClient _graphClient;
    private IReadOnlyDictionary<string, ManagedGroup>? _cachedManagedGroups;
    private readonly SemaphoreSlim _groupsLock = new(1, 1);

    public void InvalidateManagedGroupsCache()
    {
        _cachedManagedGroups = null;
    }

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
        if (_cachedManagedGroups != null)
        {
            return _cachedManagedGroups;
        }

        await _groupsLock.WaitAsync(ct);
        try
        {
            if (_cachedManagedGroups != null)
            {
                return _cachedManagedGroups;
            }

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
            _cachedManagedGroups = groupsByName;
            return _cachedManagedGroups;
        }
        finally
        {
            _groupsLock.Release();
        }
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
            var idList = string.Join("', '", chunk.Select(e => e.Value.Replace("'", "''")));
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

    public async Task ApplyBatchMutationsAsync(IEnumerable<DeltaAction> actions, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actions);

        var actionList = actions.ToList();
        if (actionList.Count == 0)
        {
            return;
        }

        var batchSize = _options.MaxBatchSize > 0 ? _options.MaxBatchSize : 20;
        _logger.LogInformation("Applying {Count} batch mutations chunked by {BatchSize}", actionList.Count, batchSize);

        foreach (var chunk in actionList.Chunk(batchSize))
        {
            var shouldFastExit = await ProcessBatchChunkAsync(chunk, ct);
            if (shouldFastExit)
            {
                return;
            }
        }
    }

    private async Task<bool> ProcessBatchChunkAsync(IReadOnlyList<DeltaAction> chunk, CancellationToken ct)
    {
        var batch = new BatchRequestContentCollection(_graphClient);
        var actionByStepId = new Dictionary<string, DeltaAction>();
        string? lastCreateUserStepId = null;

        foreach (var action in chunk)
        {
            if (action is AddGroupMemberAction addGroup && addGroup.GraphId == Guid.Empty)
            {
                if (string.IsNullOrWhiteSpace(lastCreateUserStepId))
                {
                    _logger.LogWarning(
                        "AddGroupMemberAction for group {GroupId} specifies Guid.Empty without a preceding CreateUserAction in the batch chunk. Handling gracefully.",
                        addGroup.GroupId);
                    var reqInfo = CreateRequestInformationForAction(action, null);
                    var stepId = await batch.AddBatchRequestStepAsync(reqInfo);
                    actionByStepId[stepId] = action;
                }
                else
                {
                    var reqInfo = CreateRequestInformationForAction(action, lastCreateUserStepId);
                    var stepId = await batch.AddBatchRequestStepAsync(reqInfo);
                    actionByStepId[stepId] = action;

                    if (batch.BatchRequestSteps.TryGetValue(stepId, out var batchStep))
                    {
                        batchStep.DependsOn ??= new List<string>();
                        if (!batchStep.DependsOn.Contains(lastCreateUserStepId))
                        {
                            batchStep.DependsOn.Add(lastCreateUserStepId);
                        }
                    }
                }
            }
            else
            {
                var reqInfo = CreateRequestInformationForAction(action);
                var stepId = await batch.AddBatchRequestStepAsync(reqInfo);
                actionByStepId[stepId] = action;

                if (action is CreateUserAction)
                {
                    lastCreateUserStepId = stepId;
                }
            }
        }

        var batchResponse = await _graphClient.Batch.PostAsync(batch, ct);
        var statusCodes = await batchResponse.GetResponsesStatusCodesAsync();

        var throttledActions = new List<DeltaAction>();
        int maxRetryAfterSeconds = 0;
        var unhandledFailures = new List<(string StepId, DeltaAction Action, HttpStatusCode StatusCode)>();

        foreach (var (stepId, action) in actionByStepId)
        {
            if (!statusCodes.TryGetValue(stepId, out var statusCode))
            {
                continue;
            }

            if (IsSuccessStatusCode(statusCode))
            {
                continue;
            }

            if (IsIdempotentGroupSuccess(action, statusCode))
            {
                _logger.LogDebug(
                    "Idempotent group failure tolerated for action {ActionName} on target {Target} (Status: {StatusCode})",
                    action.ActionName, action.TargetGraphId, statusCode);
                continue;
            }

            if (statusCode == HttpStatusCode.TooManyRequests)
            {
                var subResponse = await batchResponse.GetResponseByIdAsync(stepId);
                var retryAfter = ExtractRetryAfterSeconds(subResponse);
                if (retryAfter <= 0)
                {
                    retryAfter = 1;
                }

                if (retryAfter > maxRetryAfterSeconds)
                {
                    maxRetryAfterSeconds = retryAfter;
                }

                throttledActions.Add(action);
                continue;
            }

            // 5xx or unhandled non-success
            _logger.LogError(
                "ERROR_GRAPH_SUBREQUEST_FAILED: Subrequest {StepId} for action {ActionName} failed with status {StatusCode}",
                stepId, action.ActionName, statusCode);
            unhandledFailures.Add((stepId, action, statusCode));
        }

        if (throttledActions.Count > 0)
        {
            if (maxRetryAfterSeconds > _options.MaxImmediateRetryDelaySeconds)
            {
                _logger.LogWarning(
                    "WARNING_GRAPH_THROTTLE_FAST_EXIT: Long throttle detected (Retry-After: {RetryAfter}s > {MaxDelay}s). Fast-exiting without blocking.",
                    maxRetryAfterSeconds, _options.MaxImmediateRetryDelaySeconds);
                return true; // Fast-Exit
            }

            var delaySeconds = Math.Max(1, maxRetryAfterSeconds);
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);

            var retrySuccess = await RetryThrottledActionsAsync(throttledActions, ct);
            if (!retrySuccess)
            {
                _logger.LogWarning(
                    "WARNING_GRAPH_THROTTLE_FAST_EXIT: Subrequest retry failed or re-throttled. Fast-exiting without blocking.");
                return true; // Fast-Exit
            }
        }

        if (unhandledFailures.Count > 0)
        {
            var first = unhandledFailures[0];
            throw new HttpRequestException(
                $"ERROR_GRAPH_SUBREQUEST_FAILED: Subrequest {first.StepId} for action {first.Action.ActionName} failed with status {first.StatusCode}",
                null,
                first.StatusCode);
        }

        return false;
    }

    private async Task<bool> RetryThrottledActionsAsync(IReadOnlyList<DeltaAction> actions, CancellationToken ct)
    {
        var batch = new BatchRequestContentCollection(_graphClient);
        var actionByStepId = new Dictionary<string, DeltaAction>();
        string? lastCreateUserStepId = null;

        foreach (var action in actions)
        {
            if (action is AddGroupMemberAction addGroup && addGroup.GraphId == Guid.Empty)
            {
                var reqInfo = CreateRequestInformationForAction(action, lastCreateUserStepId);
                var stepId = await batch.AddBatchRequestStepAsync(reqInfo);
                actionByStepId[stepId] = action;

                if (!string.IsNullOrWhiteSpace(lastCreateUserStepId) &&
                    batch.BatchRequestSteps.TryGetValue(stepId, out var batchStep))
                {
                    batchStep.DependsOn ??= new List<string>();
                    if (!batchStep.DependsOn.Contains(lastCreateUserStepId))
                    {
                        batchStep.DependsOn.Add(lastCreateUserStepId);
                    }
                }
            }
            else
            {
                var reqInfo = CreateRequestInformationForAction(action);
                var stepId = await batch.AddBatchRequestStepAsync(reqInfo);
                actionByStepId[stepId] = action;

                if (action is CreateUserAction)
                {
                    lastCreateUserStepId = stepId;
                }
            }
        }

        var batchResponse = await _graphClient.Batch.PostAsync(batch, ct);
        var statusCodes = await batchResponse.GetResponsesStatusCodesAsync();

        foreach (var (stepId, action) in actionByStepId)
        {
            if (!statusCodes.TryGetValue(stepId, out var statusCode))
            {
                return false;
            }

            if (IsSuccessStatusCode(statusCode) || IsIdempotentGroupSuccess(action, statusCode))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private RequestInformation CreateRequestInformationForAction(DeltaAction action, string? relativeUserStepId = null) => action switch
    {
        CreateUserAction create => _graphClient.Users.ToPostRequestInformation(new User
        {
            AccountEnabled = true,
            DisplayName = create.Employee.FullName,
            MailNickname = create.UserPrincipalName.Username,
            UserPrincipalName = create.UserPrincipalName.Value,
            EmployeeId = create.Employee.Id.Value,
            PasswordProfile = new PasswordProfile
            {
                ForceChangePasswordNextSignIn = true,
                Password = create.TemporaryPassword
            }
        }),

        UpdateDisplayNameAction update => _graphClient.Users[update.GraphId.ToString()].ToPatchRequestInformation(new User
        {
            DisplayName = update.NewDisplayName
        }),

        EnableAccountAction enable => _graphClient.Users[enable.GraphId.ToString()].ToPatchRequestInformation(new User
        {
            AccountEnabled = true
        }),

        DisableAccountAction disable => _graphClient.Users[disable.GraphId.ToString()].ToPatchRequestInformation(new User
        {
            AccountEnabled = false
        }),

        RevokeSessionsAction revoke => _graphClient.Users[revoke.GraphId.ToString()].RevokeSignInSessions.ToPostRequestInformation(),

        AddGroupMemberAction addGroup => _graphClient.Groups[addGroup.GroupId.ToString()].Members.Ref.ToPostRequestInformation(new ReferenceCreate
        {
            OdataId = addGroup.GraphId == Guid.Empty && !string.IsNullOrWhiteSpace(relativeUserStepId)
                ? $"${relativeUserStepId}"
                : $"https://graph.microsoft.com/v1.0/directoryObjects/{addGroup.GraphId}"
        }),

        RemoveGroupMemberAction removeGroup => _graphClient.Groups[removeGroup.GroupId.ToString()].Members[removeGroup.GraphId.ToString()].Ref.ToDeleteRequestInformation(),

        _ => throw new NotSupportedException($"Action type {action.GetType().Name} is not supported by Graph batch mutations.")
    };

    private static bool IsSuccessStatusCode(HttpStatusCode code) =>
        (int)code >= 200 && (int)code <= 299;

    private static bool IsIdempotentGroupSuccess(DeltaAction action, HttpStatusCode code)
    {
        if (action is AddGroupMemberAction && (code == HttpStatusCode.BadRequest || code == HttpStatusCode.Conflict))
        {
            return true;
        }

        if (action is RemoveGroupMemberAction && code == HttpStatusCode.NotFound)
        {
            return true;
        }

        return false;
    }

    private static int ExtractRetryAfterSeconds(HttpResponseMessage? response)
    {
        if (response == null)
        {
            return 0;
        }

        if (response.Headers.RetryAfter != null)
        {
            if (response.Headers.RetryAfter.Delta.HasValue)
            {
                return (int)Math.Ceiling(response.Headers.RetryAfter.Delta.Value.TotalSeconds);
            }

            if (response.Headers.RetryAfter.Date.HasValue)
            {
                var diff = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
                return (int)Math.Max(1, Math.Ceiling(diff.TotalSeconds));
            }
        }

        if (response.Headers.TryGetValues("Retry-After", out var values))
        {
            var first = values.FirstOrDefault();
            if (int.TryParse(first, out var parsed))
            {
                return parsed;
            }
        }

        return 0;
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
