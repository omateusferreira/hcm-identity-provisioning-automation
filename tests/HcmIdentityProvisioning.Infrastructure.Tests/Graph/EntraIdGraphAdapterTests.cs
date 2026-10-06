using System.Net;
using System.Text.Json;
using Azure.Identity;
using FluentAssertions;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.ValueObjects;
using HcmIdentityProvisioning.Infrastructure.Graph;
using HcmIdentityProvisioning.Infrastructure.Tests.Mocks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
    public async Task ApplyBatchMutationsAsync_ThrowsNotImplementedException()
    {
        // Act
        var act = () => _sut.ApplyBatchMutationsAsync(Array.Empty<HcmIdentityProvisioning.Domain.Actions.DeltaAction>());

        // Assert
        await act.Should().ThrowAsync<NotImplementedException>()
            .WithMessage("Batch mutations will be implemented in Task 5.");
    }
}
