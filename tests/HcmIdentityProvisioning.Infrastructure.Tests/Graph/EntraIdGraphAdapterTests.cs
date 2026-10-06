using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Azure.Identity;
using FluentAssertions;
using HcmIdentityProvisioning.Domain.Actions;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Enums;
using HcmIdentityProvisioning.Domain.ValueObjects;
using HcmIdentityProvisioning.Infrastructure.Graph;
using HcmIdentityProvisioning.Infrastructure.Tests.Mocks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace HcmIdentityProvisioning.Infrastructure.Tests.Graph;

public sealed class EntraIdGraphAdapterTests
{
    private readonly MockHttpMessageHandler _mockHandler;
    private readonly EntraIdGraphOptions _options;
    private readonly EntraIdGraphAdapter _sut;

    public EntraIdGraphAdapterTests()
    {
        _mockHandler = new MockHttpMessageHandler();
        _options = new EntraIdGraphOptions
        {
            TenantDomain = "contoso.com",
            ManagedGroupPrefix = "grp-iam-",
            FilterChunkSize = 15,
            CustomCredential = new MockTokenCredential()
        };

        var optionsWrapper = Options.Create(_options);
        var logger = NullLogger<EntraIdGraphAdapter>.Instance;
        _sut = new EntraIdGraphAdapter(optionsWrapper, logger, _mockHandler.ToHttpClient());
    }

    [Fact]
    public async Task GetManagedGroupsAsync_FiltersOnlyManagedPrefix_CaseInsensitive()
    {
        // Arrange
        var group1Id = Guid.NewGuid();
        var group2Id = Guid.NewGuid();
        var unmanagedId = Guid.NewGuid();

        var groupsJson = JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new { id = group1Id.ToString(), displayName = "grp-iam-engineering" },
                new { id = group2Id.ToString(), displayName = "GRP-IAM-FINANCE" },
                new { id = unmanagedId.ToString(), displayName = "unmanaged-group" }
            }
        });

        _mockHandler.SetupGroups(groupsJson);

        // Act
        var result = await _sut.GetManagedGroupsAsync();

        // Assert
        result.Should().HaveCount(2);
        result.Should().ContainKey("grp-iam-engineering");
        result.Should().ContainKey("GRP-IAM-FINANCE");
        result.Should().NotContainKey("unmanaged-group");

        // Case-insensitivity check
        result.ContainsKey("GRP-IAM-ENGINEERING").Should().BeTrue();
        result.ContainsKey("grp-iam-finance").Should().BeTrue();
        result["GRP-IAM-ENGINEERING"].Id.Should().Be(group1Id);
        result["grp-iam-finance"].Id.Should().Be(group2Id);

        // Verify sent request URL query parameters
        var groupRequest = _mockHandler.LastRequest;
        groupRequest.Should().NotBeNull();
        var rawUri = Uri.UnescapeDataString(groupRequest!.RequestUri!.ToString());
        rawUri.Should().Contain("$filter=startswith(displayName, 'grp-iam-')");
        rawUri.Should().Contain("$select=id,displayName");
    }

    [Fact]
    public async Task IsUserPrincipalNameAvailableAsync_WhenEmpty_ReturnsTrue_WithSelectId()
    {
        // Arrange
        _mockHandler.SetupUsers(JsonSerializer.Serialize(new { value = Array.Empty<object>() }));
        var upn = UserPrincipalName.Create("available.user@contoso.com").Value;

        // Act
        var isAvailable = await _sut.IsUserPrincipalNameAvailableAsync(upn);

        // Assert
        isAvailable.Should().BeTrue();

        var lastRequest = _mockHandler.LastRequest;
        lastRequest.Should().NotBeNull();
        var rawUri = Uri.UnescapeDataString(lastRequest!.RequestUri!.ToString());
        rawUri.Should().Contain("$filter=userPrincipalName eq 'available.user@contoso.com'");
        rawUri.Should().Contain("$select=id");
    }

    [Fact]
    public async Task IsUserPrincipalNameAvailableAsync_WhenExists_ReturnsFalse()
    {
        // Arrange
        var existingUserJson = JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new { id = Guid.NewGuid().ToString() }
            }
        });
        _mockHandler.SetupUsers(existingUserJson);
        var upn = UserPrincipalName.Create("existing.user@contoso.com").Value;

        // Act
        var isAvailable = await _sut.IsUserPrincipalNameAvailableAsync(upn);

        // Assert
        isAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task GetUsersByEmployeeIdsAsync_SplitsIntoChunksOf15_AndFiltersManagedGroups()
    {
        // Arrange: 35 employee IDs
        var employeeIds = Enumerable.Range(1, 35)
            .Select(i => EmployeeId.Create($"EMP-{i:D4}").Value)
            .ToList();

        var managedGroupId1 = Guid.NewGuid();
        var managedGroupId2 = Guid.NewGuid();
        var unmanagedGroupId = Guid.NewGuid();

        // 1. Managed groups setup
        var groupsJson = JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new { id = managedGroupId1.ToString(), displayName = "grp-iam-engineering" },
                new { id = managedGroupId2.ToString(), displayName = "grp-iam-hr" },
                new { id = unmanagedGroupId.ToString(), displayName = "unmanaged-general" }
            }
        });
        _mockHandler.SetupGroups(groupsJson);

        // 2. Target users that will be returned
        var user1GraphId = Guid.NewGuid();
        var user2GraphId = Guid.NewGuid();

        // Register memberOf routes specifically before generic /users route
        _mockHandler.When(req => req.RequestUri != null && req.RequestUri.ToString().Contains($"/users/{user1GraphId}/memberOf", StringComparison.OrdinalIgnoreCase))
            .RespondWithJson(JsonSerializer.Serialize(new
            {
                value = new[]
                {
                    new { id = managedGroupId1.ToString(), displayName = "grp-iam-engineering" },
                    new { id = unmanagedGroupId.ToString(), displayName = "unmanaged-general" }
                }
            }));

        _mockHandler.When(req => req.RequestUri != null && req.RequestUri.ToString().Contains($"/users/{user2GraphId}/memberOf", StringComparison.OrdinalIgnoreCase))
            .RespondWithJson(JsonSerializer.Serialize(new
            {
                value = new[]
                {
                    new { id = managedGroupId2.ToString(), displayName = "grp-iam-hr" }
                }
            }));

        // Fallback for any other user's memberOf
        _mockHandler.When(req => req.RequestUri != null && req.RequestUri.ToString().Contains("/memberOf", StringComparison.OrdinalIgnoreCase))
            .RespondWithJson(JsonSerializer.Serialize(new { value = Array.Empty<object>() }));

        // 3. User search query handler
        _mockHandler.When(req => req.RequestUri != null && req.RequestUri.AbsolutePath.EndsWith("/users", StringComparison.OrdinalIgnoreCase))
            .RespondWith(req =>
            {
                var uri = Uri.UnescapeDataString(req.RequestUri!.ToString());
                var matchedUsers = new List<object>();

                if (uri.Contains("EMP-0001"))
                {
                    matchedUsers.Add(new
                    {
                        id = user1GraphId.ToString(),
                        employeeId = "EMP-0001",
                        userPrincipalName = "user.one@contoso.com",
                        displayName = "User One",
                        accountEnabled = true
                    });
                }

                if (uri.Contains("EMP-0020"))
                {
                    matchedUsers.Add(new
                    {
                        id = user2GraphId.ToString(),
                        employeeId = "EMP-0020",
                        userPrincipalName = "user.twenty@contoso.com",
                        displayName = "User Twenty",
                        accountEnabled = false
                    });
                }

                return MockHttpMessageHandler.CreateJsonResponse(JsonSerializer.Serialize(new { value = matchedUsers }));
            });

        // Act
        var result = await _sut.GetUsersByEmployeeIdsAsync(employeeIds);

        // Assert
        // A) Verify chunking: 35 IDs chunked by 15 => 3 user queries
        var userQueries = _mockHandler.SentRequests
            .Where(r => r.RequestUri != null && r.RequestUri.AbsolutePath.EndsWith("/users", StringComparison.OrdinalIgnoreCase))
            .ToList();

        userQueries.Should().HaveCount(3);

        foreach (var req in userQueries)
        {
            var rawUri = Uri.UnescapeDataString(req.RequestUri!.ToString());
            rawUri.Should().Contain("$select=id,employeeId,userPrincipalName,displayName,accountEnabled");
            rawUri.Should().Contain("employeeId in (");
        }

        // B) Verify matched users mapping
        result.Should().HaveCount(2);

        var emp1 = EmployeeId.Create("EMP-0001").Value;
        result.Should().ContainKey(emp1);
        var user1 = result[emp1];
        user1.GraphId.Should().Be(user1GraphId);
        user1.DisplayName.Should().Be("User One");
        user1.AccountEnabled.Should().BeTrue();
        // Crucial: unmanagedGroupId (unmanaged-general) was stripped! Only managedGroupId1 remains.
        user1.AssignedGroupIds.Should().ContainSingle().Which.Should().Be(managedGroupId1);

        var emp20 = EmployeeId.Create("EMP-0020").Value;
        result.Should().ContainKey(emp20);
        var user2 = result[emp20];
        user2.GraphId.Should().Be(user2GraphId);
        user2.DisplayName.Should().Be("User Twenty");
        user2.AccountEnabled.Should().BeFalse();
        user2.AssignedGroupIds.Should().ContainSingle().Which.Should().Be(managedGroupId2);
    }

    [Fact]
    public async Task GetUsersByEmployeeIdsAsync_WhenEmptyList_ReturnsEmptyWithoutNetworkCalls()
    {
        // Act
        var result = await _sut.GetUsersByEmployeeIdsAsync(Array.Empty<EmployeeId>());

        // Assert
        result.Should().BeEmpty();
        _mockHandler.SentRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task GetManagedGroupsAsync_SupportsPagination_WhenOdataNextLinkPresent()
    {
        // Arrange
        var g1 = Guid.NewGuid();
        var g2 = Guid.NewGuid();

        var page1Json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["@odata.nextLink"] = "https://graph.microsoft.com/v1.0/groups?$skiptoken=page2token",
            ["value"] = new[]
            {
                new { id = g1.ToString(), displayName = "grp-iam-first-page" }
            }
        });

        var page2Json = JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new { id = g2.ToString(), displayName = "grp-iam-second-page" }
            }
        });

        _mockHandler.When(r => r.RequestUri != null && r.RequestUri.ToString().Contains("$skiptoken=page2token"))
            .RespondWithJson(page2Json);

        _mockHandler.SetupGroups(page1Json);

        // Act
        var result = await _sut.GetManagedGroupsAsync();

        // Assert
        result.Should().HaveCount(2);
        result.Should().ContainKey("grp-iam-first-page");
        result.Should().ContainKey("grp-iam-second-page");
    }

    [Fact]
    public async Task ApplyBatchMutationsAsync_BuildsValidBatchPayload_For7ActionTypes()
    {
        // Arrange
        var user2Id = Guid.NewGuid();
        var user3Id = Guid.NewGuid();
        var user4Id = Guid.NewGuid();
        var user5Id = Guid.NewGuid();
        var user6Id = Guid.NewGuid();
        var user7Id = Guid.NewGuid();
        var group1Id = Guid.NewGuid();
        var group2Id = Guid.NewGuid();

        var emp = new Employee(
            EmployeeId.Create("EMP-1001").Value,
            "Alice Smith",
            EmployeeStatus.Active,
            "Engineering",
            "Software Engineer",
            new Dictionary<string, string>());
        var upn = UserPrincipalName.Create("alice.smith@contoso.com").Value;

        var actions = new DeltaAction[]
        {
            new CreateUserAction(emp, upn, "TempPass123!"),
            new UpdateDisplayNameAction(user2Id, "Bob Jones Updated"),
            new EnableAccountAction(user3Id),
            new DisableAccountAction(user4Id),
            new RevokeSessionsAction(user5Id),
            new AddGroupMemberAction(user6Id, group1Id, "grp-iam-eng"),
            new RemoveGroupMemberAction(user7Id, group2Id, "grp-iam-hr")
        };

        _mockHandler.WhenBatch().RespondWith(req =>
        {
            var doc = JsonDocument.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var requests = doc.RootElement.GetProperty("requests");
            var subResponses = new List<MockBatchSubResponse>();
            foreach (var r in requests.EnumerateArray())
            {
                var id = r.GetProperty("id").GetString()!;
                subResponses.Add(new MockBatchSubResponse(id, 200));
            }
            return MockHttpMessageHandler.CreateJsonResponse(MockHttpMessageHandler.CreateBatchResponseBody(subResponses));
        });

        // Act
        await _sut.ApplyBatchMutationsAsync(actions);

        // Assert
        _mockHandler.LastRequestBody.Should().NotBeNullOrWhiteSpace();
        var batchDoc = JsonDocument.Parse(_mockHandler.LastRequestBody!);
        var requestsArray = batchDoc.RootElement.GetProperty("requests").EnumerateArray().ToList();
        requestsArray.Should().HaveCount(7);

        // 1. CreateUser: POST /users com passwordProfile (forceChangePasswordNextSignIn: true)
        var createReq = requestsArray[0];
        createReq.GetProperty("method").GetString().Should().Be("POST");
        createReq.GetProperty("url").GetString().Should().Be("/users");
        var createBody = createReq.GetProperty("body");
        createBody.GetProperty("displayName").GetString().Should().Be("Alice Smith");
        createBody.GetProperty("userPrincipalName").GetString().Should().Be("alice.smith@contoso.com");
        createBody.GetProperty("mailNickname").GetString().Should().Be("alice.smith");
        createBody.GetProperty("passwordProfile").GetProperty("forceChangePasswordNextSignIn").GetBoolean().Should().BeTrue();
        createBody.GetProperty("passwordProfile").GetProperty("password").GetString().Should().Be("TempPass123!");

        // 2. UpdateDisplayName: PATCH /users/{id} com displayName
        var updateReq = requestsArray[1];
        updateReq.GetProperty("method").GetString().Should().Be("PATCH");
        updateReq.GetProperty("url").GetString().Should().Be($"/users/{user2Id}");
        updateReq.GetProperty("body").GetProperty("displayName").GetString().Should().Be("Bob Jones Updated");

        // 3. EnableAccount: PATCH /users/{id} com accountEnabled: true
        var enableReq = requestsArray[2];
        enableReq.GetProperty("method").GetString().Should().Be("PATCH");
        enableReq.GetProperty("url").GetString().Should().Be($"/users/{user3Id}");
        enableReq.GetProperty("body").GetProperty("accountEnabled").GetBoolean().Should().BeTrue();

        // 4. DisableAccount: PATCH /users/{id} com accountEnabled: false
        var disableReq = requestsArray[3];
        disableReq.GetProperty("method").GetString().Should().Be("PATCH");
        disableReq.GetProperty("url").GetString().Should().Be($"/users/{user4Id}");
        disableReq.GetProperty("body").GetProperty("accountEnabled").GetBoolean().Should().BeFalse();

        // 5. RevokeSessions: POST /users/{id}/revokeSignInSessions
        var revokeReq = requestsArray[4];
        revokeReq.GetProperty("method").GetString().Should().Be("POST");
        revokeReq.GetProperty("url").GetString().Should().Be($"/users/{user5Id}/revokeSignInSessions");

        // 6. AddGroupMember: POST /groups/{groupId}/members/$ref com @odata.id
        var addGroupReq = requestsArray[5];
        addGroupReq.GetProperty("method").GetString().Should().Be("POST");
        addGroupReq.GetProperty("url").GetString().Should().Be($"/groups/{group1Id}/members/$ref");
        addGroupReq.GetProperty("body").GetProperty("@odata.id").GetString().Should().Be($"https://graph.microsoft.com/v1.0/directoryObjects/{user6Id}");

        // 7. RemoveGroupMember: DELETE /groups/{groupId}/members/{userId}/$ref
        var removeGroupReq = requestsArray[6];
        removeGroupReq.GetProperty("method").GetString().Should().Be("DELETE");
        removeGroupReq.GetProperty("url").GetString().Should().Be($"/groups/{group2Id}/members/{user7Id}/$ref");
    }

    [Fact]
    public async Task ApplyBatchMutationsAsync_OnMicroRetryThrottle_RetriesFailedItem_Successfully()
    {
        // Arrange
        var user1Id = Guid.NewGuid();
        var user2Id = Guid.NewGuid();
        var action1 = new EnableAccountAction(user1Id);
        var action2 = new DisableAccountAction(user2Id);

        int batchCallCount = 0;
        _mockHandler.WhenBatch().RespondWith(req =>
        {
            batchCallCount++;
            var doc = JsonDocument.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var requests = doc.RootElement.GetProperty("requests").EnumerateArray().ToList();

            if (batchCallCount == 1)
            {
                // First batch: action1 succeeds (200), action2 gets throttled 429 with Retry-After: 1
                var step1Id = requests[0].GetProperty("id").GetString()!;
                var step2Id = requests[1].GetProperty("id").GetString()!;
                return MockHttpMessageHandler.CreateJsonResponse(MockHttpMessageHandler.CreateBatchResponseBody(
                    new MockBatchSubResponse(step1Id, 200),
                    new MockBatchSubResponse(step2Id, 429, Headers: new Dictionary<string, string> { ["Retry-After"] = "1" })
                ));
            }
            else
            {
                // Second batch (retry): action2 succeeds (200)
                var retryStepId = requests[0].GetProperty("id").GetString()!;
                return MockHttpMessageHandler.CreateJsonResponse(MockHttpMessageHandler.CreateBatchResponseBody(
                    new MockBatchSubResponse(retryStepId, 200)
                ));
            }
        });

        // Act
        var sw = Stopwatch.StartNew();
        await _sut.ApplyBatchMutationsAsync(new DeltaAction[] { action1, action2 });
        sw.Stop();

        // Assert
        // Should have retried exactly the throttled action in a 2nd batch after ~1 second
        batchCallCount.Should().Be(2);
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(900);

        // Verify the 2nd batch only contained action2
        var secondBatchBody = _mockHandler.GetSentRequestBody(1);
        var secondDoc = JsonDocument.Parse(secondBatchBody);
        var secondRequests = secondDoc.RootElement.GetProperty("requests").EnumerateArray().ToList();
        secondRequests.Should().HaveCount(1);
        secondRequests[0].GetProperty("url").GetString().Should().Be($"/users/{user2Id}");
    }

    [Fact]
    public async Task ApplyBatchMutationsAsync_OnLongThrottle_TriggersFastExit_WithoutBlocking()
    {
        // Arrange
        var mockLogger = Substitute.For<ILogger<EntraIdGraphAdapter>>();
        var adapterWithMockLogger = new EntraIdGraphAdapter(
            Options.Create(_options),
            mockLogger,
            _mockHandler.ToHttpClient()
        );

        var user1Id = Guid.NewGuid();
        var action = new EnableAccountAction(user1Id);

        _mockHandler.WhenBatch().RespondWith(req =>
        {
            var doc = JsonDocument.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var requests = doc.RootElement.GetProperty("requests").EnumerateArray().ToList();
            var stepId = requests[0].GetProperty("id").GetString()!;

            return MockHttpMessageHandler.CreateJsonResponse(MockHttpMessageHandler.CreateBatchResponseBody(
                new MockBatchSubResponse(stepId, 429, Headers: new Dictionary<string, string> { ["Retry-After"] = "60" })
            ));
        });

        // Act
        var sw = Stopwatch.StartNew();
        await adapterWithMockLogger.ApplyBatchMutationsAsync(new[] { action });
        sw.Stop();

        // Assert
        // Fast-Exit must not block for 60s
        sw.ElapsedMilliseconds.Should().BeLessThan(5000);

        // Structured log WARNING_GRAPH_THROTTLE_FAST_EXIT emitted
        mockLogger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("WARNING_GRAPH_THROTTLE_FAST_EXIT")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>()
        );

        // No retry batch sent
        _mockHandler.SentRequests.Where(r => r.RequestUri != null && r.RequestUri.ToString().Contains("/$batch")).Should().HaveCount(1);
    }

    [Fact]
    public async Task ApplyBatchMutationsAsync_ToleratesIdempotentGroupFailures_400_and_404()
    {
        // Arrange
        var user1Id = Guid.NewGuid();
        var user2Id = Guid.NewGuid();
        var user3Id = Guid.NewGuid();
        var group1Id = Guid.NewGuid();
        var group2Id = Guid.NewGuid();
        var group3Id = Guid.NewGuid();

        var action1 = new AddGroupMemberAction(user1Id, group1Id, "grp-iam-1");
        var action2 = new AddGroupMemberAction(user2Id, group2Id, "grp-iam-2");
        var action3 = new RemoveGroupMemberAction(user3Id, group3Id, "grp-iam-3");

        _mockHandler.WhenBatch().RespondWith(req =>
        {
            var doc = JsonDocument.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var requests = doc.RootElement.GetProperty("requests").EnumerateArray().ToList();

            var step1Id = requests[0].GetProperty("id").GetString()!;
            var step2Id = requests[1].GetProperty("id").GetString()!;
            var step3Id = requests[2].GetProperty("id").GetString()!;

            return MockHttpMessageHandler.CreateJsonResponse(MockHttpMessageHandler.CreateBatchResponseBody(
                new MockBatchSubResponse(step1Id, 400), // Bad Request (already member)
                new MockBatchSubResponse(step2Id, 409), // Conflict (already member)
                new MockBatchSubResponse(step3Id, 404)  // Not Found (already removed / not member)
            ));
        });

        // Act
        var act = () => _sut.ApplyBatchMutationsAsync(new DeltaAction[] { action1, action2, action3 });

        // Assert: must complete cleanly without throwing
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ApplyBatchMutationsAsync_WhenEmpty_ReturnsWithoutNetworkCalls()
    {
        // Act
        await _sut.ApplyBatchMutationsAsync(Array.Empty<DeltaAction>());

        // Assert
        _mockHandler.SentRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyBatchMutationsAsync_SplitsIntoChunksOfMaxBatchSize()
    {
        // Arrange: 25 actions with MaxBatchSize = 20
        var actions = Enumerable.Range(1, 25)
            .Select(_ => (DeltaAction)new EnableAccountAction(Guid.NewGuid()))
            .ToList();

        _mockHandler.WhenBatch().RespondWith(req =>
        {
            var doc = JsonDocument.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var requests = doc.RootElement.GetProperty("requests");
            var subResponses = new List<MockBatchSubResponse>();
            foreach (var r in requests.EnumerateArray())
            {
                var id = r.GetProperty("id").GetString()!;
                subResponses.Add(new MockBatchSubResponse(id, 200));
            }
            return MockHttpMessageHandler.CreateJsonResponse(MockHttpMessageHandler.CreateBatchResponseBody(subResponses));
        });

        // Act
        await _sut.ApplyBatchMutationsAsync(actions);

        // Assert: 25 items chunked by 20 -> 2 batch requests (first 20, second 5)
        var batchRequests = _mockHandler.SentRequests
            .Where(r => r.RequestUri != null && r.RequestUri.ToString().Contains("/$batch"))
            .ToList();
        batchRequests.Should().HaveCount(2);

        var firstBatch = JsonDocument.Parse(_mockHandler.GetSentRequestBody(0)).RootElement.GetProperty("requests").EnumerateArray().ToList();
        firstBatch.Should().HaveCount(20);

        var secondBatch = JsonDocument.Parse(_mockHandler.GetSentRequestBody(1)).RootElement.GetProperty("requests").EnumerateArray().ToList();
        secondBatch.Should().HaveCount(5);
    }
}
