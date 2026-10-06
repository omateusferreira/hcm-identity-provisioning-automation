using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Application.UseCases;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Infrastructure.Connectors.Rest;
using HcmIdentityProvisioning.Infrastructure.DependencyInjection;
using HcmIdentityProvisioning.Infrastructure.Graph;
using HcmIdentityProvisioning.Infrastructure.Notifications;
using HcmIdentityProvisioning.Infrastructure.Tests.Mocks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Xunit;

namespace HcmIdentityProvisioning.Infrastructure.Tests.EndToEnd;

public sealed class ProductionPipelineIntegrationTests
{
    private const string HcmEmployeesJson = """
    {
      "items": [
        {
          "id": "EMP-001",
          "fullName": "Mariana Lima",
          "status": "Active",
          "department": "Tecnologia",
          "jobTitle": "Engenheiro de Software",
          "extendedAttributes": {
            "email": "mariana.personal@example.com"
          }
        },
        {
          "id": "EMP-002",
          "fullName": "Rodrigo Alves",
          "status": "Active",
          "department": "Financeiro",
          "jobTitle": "Analista Financeiro",
          "extendedAttributes": {
            "email": "rodrigo.alves@example.com"
          }
        },
        {
          "id": "EMP-003",
          "fullName": "Beatriz Souza",
          "status": "Inactive",
          "department": "Marketing",
          "jobTitle": "Coordenadora",
          "extendedAttributes": {}
        },
        {
          "id": "EMP-004",
          "fullName": "Mariana Lima",
          "status": "Active",
          "department": "Recursos Humanos",
          "jobTitle": "Analista de RH",
          "extendedAttributes": {
            "email": "mariana.lima2@example.com"
          }
        },
        {
          "id": "EMP-005",
          "fullName": "José d'Ávila",
          "status": "Active",
          "department": "Tecnologia",
          "jobTitle": "Engenheiro de Dados",
          "extendedAttributes": {
            "email": "jose.davila@example.com"
          }
        },
        {
          "id": "EMP-006",
          "fullName": "Felipe Gomes",
          "status": "Active",
          "department": "Tecnologia",
          "jobTitle": "Engenheiro DevOps",
          "extendedAttributes": {}
        }
      ],
      "pageNumber": 1,
      "pageSize": 50,
      "totalCount": 6
    }
    """;

    private static string ResolveRepoFile(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
            current = current.Parent;
        }
        return relativePath;
    }

    private sealed class SimulatedGraphDirectory
    {
        public Guid EngineeringGroupId { get; } = Guid.NewGuid();
        public Guid FinanceGroupId { get; } = Guid.NewGuid();
        public Guid AllStaffGroupId { get; } = Guid.NewGuid();

        public Guid MoverGraphId { get; } = Guid.NewGuid();
        public Guid LeaverGraphId { get; } = Guid.NewGuid();
        public Guid NoopGraphId { get; } = Guid.NewGuid();

        public sealed class SimulatedUser
        {
            public Guid Id { get; set; }
            public string EmployeeId { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public string UserPrincipalName { get; set; } = string.Empty;
            public bool AccountEnabled { get; set; }
            public HashSet<Guid> MemberOfGroupIds { get; } = new();
        }

        public Dictionary<Guid, SimulatedUser> Users { get; } = new();
        public int BatchRequestsReceived { get; set; }
        public List<string> CapturedBatchBodies { get; } = new();

        public SimulatedGraphDirectory()
        {
            // EMP-002 Rodrigo Alves (Mover): initially active in engineering only
            var mover = new SimulatedUser
            {
                Id = MoverGraphId,
                EmployeeId = "EMP-002",
                DisplayName = "Rodrigo Alves",
                UserPrincipalName = "rodrigo.alves@company.onmicrosoft.com",
                AccountEnabled = true
            };
            mover.MemberOfGroupIds.Add(EngineeringGroupId);
            Users[mover.Id] = mover;

            // EMP-003 Beatriz Souza (Leaver): initially active with all-staff group
            var leaver = new SimulatedUser
            {
                Id = LeaverGraphId,
                EmployeeId = "EMP-003",
                DisplayName = "Beatriz Souza",
                UserPrincipalName = "beatriz.souza@company.onmicrosoft.com",
                AccountEnabled = true
            };
            leaver.MemberOfGroupIds.Add(AllStaffGroupId);
            Users[leaver.Id] = leaver;

            // EMP-006 Felipe Gomes (Idempotent No-Op): initially active with engineering & all-staff
            var noop = new SimulatedUser
            {
                Id = NoopGraphId,
                EmployeeId = "EMP-006",
                DisplayName = "Felipe Gomes",
                UserPrincipalName = "felipe.gomes@company.onmicrosoft.com",
                AccountEnabled = true
            };
            noop.MemberOfGroupIds.Add(EngineeringGroupId);
            noop.MemberOfGroupIds.Add(AllStaffGroupId);
            Users[noop.Id] = noop;
        }
    }

    private static (ServiceProvider ServiceProvider, SimulatedGraphDirectory Directory, MockHttpMessageHandler HcmMock, MockHttpMessageHandler GraphMock)
        BuildPipelineServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug));

        var directory = new SimulatedGraphDirectory();
        var hcmMockHandler = new MockHttpMessageHandler();
        var graphMockHandler = new MockHttpMessageHandler();

        // Setup HCM mock endpoint
        hcmMockHandler.WhenUrlContains("/api/v1/employees")
            .RespondWithJson(HcmEmployeesJson);

        // 1. Graph mock: Batch endpoint
        graphMockHandler.When(req => req.RequestUri != null && req.RequestUri.ToString().Contains("/$batch", StringComparison.OrdinalIgnoreCase))
            .RespondWith(req =>
            {
                directory.BatchRequestsReceived++;
                var body = req.Content != null
                    ? req.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    : string.Empty;
                directory.CapturedBatchBodies.Add(body);

                using var doc = JsonDocument.Parse(body);
                var requests = doc.RootElement.GetProperty("requests");
                var subResponses = new List<MockBatchSubResponse>();

                SimulatedGraphDirectory.SimulatedUser? lastCreatedUser = null;

                foreach (var r in requests.EnumerateArray())
                {
                    var id = r.GetProperty("id").GetString()!;
                    var method = r.GetProperty("method").GetString()!;
                    var url = r.GetProperty("url").GetString()!;
                    subResponses.Add(new MockBatchSubResponse(id, 200));

                    var trimmedUrl = url.TrimStart('/');
                    var segments = trimmedUrl.Split('/', StringSplitOptions.RemoveEmptyEntries);

                    if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(trimmedUrl, "users", StringComparison.OrdinalIgnoreCase))
                    {
                        var bodyElem = r.GetProperty("body");
                        var empId = bodyElem.GetProperty("employeeId").GetString()!;
                        var dispName = bodyElem.GetProperty("displayName").GetString()!;
                        var upn = bodyElem.GetProperty("userPrincipalName").GetString()!;

                        lastCreatedUser = new SimulatedGraphDirectory.SimulatedUser
                        {
                            Id = Guid.NewGuid(),
                            EmployeeId = empId,
                            DisplayName = dispName,
                            UserPrincipalName = upn,
                            AccountEnabled = true
                        };
                        directory.Users[lastCreatedUser.Id] = lastCreatedUser;
                    }
                    else if (string.Equals(method, "PATCH", StringComparison.OrdinalIgnoreCase) &&
                             segments.Length == 2 &&
                             string.Equals(segments[0], "users", StringComparison.OrdinalIgnoreCase) &&
                             Guid.TryParse(segments[1], out var patchUserId))
                    {
                        if (directory.Users.TryGetValue(patchUserId, out var user))
                        {
                            var bodyElem = r.GetProperty("body");
                            if (bodyElem.TryGetProperty("accountEnabled", out var enabledElem))
                            {
                                user.AccountEnabled = enabledElem.GetBoolean();
                            }
                            if (bodyElem.TryGetProperty("displayName", out var nameElem))
                            {
                                user.DisplayName = nameElem.GetString() ?? user.DisplayName;
                            }
                        }
                    }
                    else if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) &&
                             segments.Length == 4 &&
                             string.Equals(segments[0], "groups", StringComparison.OrdinalIgnoreCase) &&
                             Guid.TryParse(segments[1], out var addGroupId) &&
                             string.Equals(segments[2], "members", StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(segments[3], "$ref", StringComparison.OrdinalIgnoreCase))
                    {
                        var bodyElem = r.GetProperty("body");
                        if (bodyElem.TryGetProperty("@odata.id", out var odataElem))
                        {
                            var odataId = odataElem.GetString() ?? string.Empty;
                            if (odataId.StartsWith('$') && lastCreatedUser != null)
                            {
                                lastCreatedUser.MemberOfGroupIds.Add(addGroupId);
                            }
                            else
                            {
                                var lastSeg = odataId.Split('/').Last();
                                if (Guid.TryParse(lastSeg, out var targetUserId))
                                {
                                    if (targetUserId == Guid.Empty && lastCreatedUser != null)
                                    {
                                        lastCreatedUser.MemberOfGroupIds.Add(addGroupId);
                                    }
                                    else if (directory.Users.TryGetValue(targetUserId, out var user))
                                    {
                                        user.MemberOfGroupIds.Add(addGroupId);
                                    }
                                }
                            }
                        }
                    }
                    else if (string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase) &&
                             segments.Length == 5 &&
                             string.Equals(segments[0], "groups", StringComparison.OrdinalIgnoreCase) &&
                             Guid.TryParse(segments[1], out var remGroupId) &&
                             string.Equals(segments[2], "members", StringComparison.OrdinalIgnoreCase) &&
                             Guid.TryParse(segments[3], out var remUserId) &&
                             string.Equals(segments[4], "$ref", StringComparison.OrdinalIgnoreCase))
                    {
                        if (directory.Users.TryGetValue(remUserId, out var user))
                        {
                            user.MemberOfGroupIds.Remove(remGroupId);
                        }
                    }
                }

                return MockHttpMessageHandler.CreateJsonResponse(MockHttpMessageHandler.CreateBatchResponseBody(subResponses));
            });

        // 2. Graph mock: MemberOf endpoint
        graphMockHandler.When(req => req.RequestUri != null && req.RequestUri.ToString().Contains("/memberOf", StringComparison.OrdinalIgnoreCase))
            .RespondWith(req =>
            {
                var uri = req.RequestUri!.ToString();
                var match = Regex.Match(uri, @"/users/([^/]+)/memberOf", RegexOptions.IgnoreCase);
                if (match.Success && Guid.TryParse(match.Groups[1].Value, out var userId) && directory.Users.TryGetValue(userId, out var user))
                {
                    var groupRefs = user.MemberOfGroupIds.Select(g => new { id = g.ToString() }).ToList();
                    return MockHttpMessageHandler.CreateJsonResponse(MockHttpMessageHandler.CreateODataCollectionResponse(groupRefs));
                }

                return MockHttpMessageHandler.CreateJsonResponse(MockHttpMessageHandler.CreateODataCollectionResponse(Array.Empty<object>()));
            });

        // 3. Graph mock: Users endpoint with userPrincipalName query
        graphMockHandler.When(req => req.RequestUri != null &&
                                     req.RequestUri.ToString().Contains("/users", StringComparison.OrdinalIgnoreCase) &&
                                     Uri.UnescapeDataString(req.RequestUri.ToString()).Contains("userPrincipalName eq", StringComparison.OrdinalIgnoreCase))
            .RespondWith(req =>
            {
                var unescaped = Uri.UnescapeDataString(req.RequestUri!.ToString());
                var match = Regex.Match(unescaped, @"userPrincipalName eq '([^']+)'", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var targetUpn = match.Groups[1].Value;
                    var existing = directory.Users.Values.FirstOrDefault(u => string.Equals(u.UserPrincipalName, targetUpn, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        return MockHttpMessageHandler.CreateJsonResponse(MockHttpMessageHandler.CreateODataCollectionResponse(new[]
                        {
                            new { id = existing.Id.ToString() }
                        }));
                    }
                }

                return MockHttpMessageHandler.CreateJsonResponse(MockHttpMessageHandler.CreateODataCollectionResponse(Array.Empty<object>()));
            });

        // 4. Graph mock: Users endpoint with employeeId query
        graphMockHandler.When(req => req.RequestUri != null &&
                                     req.RequestUri.ToString().Contains("/users", StringComparison.OrdinalIgnoreCase) &&
                                     Uri.UnescapeDataString(req.RequestUri.ToString()).Contains("employeeId", StringComparison.OrdinalIgnoreCase))
            .RespondWith(req =>
            {
                var unescaped = Uri.UnescapeDataString(req.RequestUri!.ToString());
                var foundUsers = directory.Users.Values
                    .Where(u => unescaped.Contains($"'{u.EmployeeId}'", StringComparison.OrdinalIgnoreCase))
                    .Select(u => new
                    {
                        id = u.Id.ToString(),
                        employeeId = u.EmployeeId,
                        displayName = u.DisplayName,
                        userPrincipalName = u.UserPrincipalName,
                        accountEnabled = u.AccountEnabled
                    })
                    .ToList();

                return MockHttpMessageHandler.CreateJsonResponse(MockHttpMessageHandler.CreateODataCollectionResponse(foundUsers));
            });

        // 5. Graph mock: Groups endpoint
        graphMockHandler.When(req => req.RequestUri != null &&
                                     req.RequestUri.ToString().Contains("/groups", StringComparison.OrdinalIgnoreCase))
            .RespondWith(_ =>
            {
                var groups = new[]
                {
                    new { id = directory.EngineeringGroupId.ToString(), displayName = "grp-iam-engineering" },
                    new { id = directory.FinanceGroupId.ToString(), displayName = "grp-iam-finance" },
                    new { id = directory.AllStaffGroupId.ToString(), displayName = "grp-iam-all-staff" }
                };

                return MockHttpMessageHandler.CreateJsonResponse(MockHttpMessageHandler.CreateODataCollectionResponse(groups));
            });

        // Sync Settings & Core Registration
        var settings = new SyncSettings
        {
            TenantDomain = "company.onmicrosoft.com",
            ManagedGroupPrefix = "grp-iam-",
            MaxDisablementPercentage = 50.0,
            MaxDisablementCount = 100,
            BatchPageSize = 50
        };

        var rulesPath = ResolveRepoFile("src/HcmIdentityProvisioning.Infrastructure/Rules/rules.json");
        services.AddHcmProvisioningCore(settings, rulesPath);

        // Generic REST HCM Connector wired to hcmMockHandler
        services.AddGenericRestHcmConnector(options =>
        {
            options.BaseUrl = "https://api.hcm.example.com";
            options.Endpoint = "/api/v1/employees";
            options.AuthScheme = HcmAuthScheme.Bearer;
            options.BearerToken = "test-hcm-token";
        });
        services.AddHttpClient(nameof(GenericRestHcmConnector))
            .ConfigurePrimaryHttpMessageHandler(() => hcmMockHandler);

        // Entra ID Graph Adapter wired to graphMockHandler
        var graphClient = new GraphServiceClient(graphMockHandler.ToHttpClient(), new MockTokenCredential());
        services.AddSingleton(graphClient);
        services.AddEntraIdGraphAdapter(options =>
        {
            options.TenantDomain = "company.onmicrosoft.com";
            options.ManagedGroupPrefix = "grp-iam-";
            options.CustomCredential = new MockTokenCredential();
        });

        // Credential Delivery Service
        services.AddSingleton<ICredentialDeliveryService, MockCredentialDeliveryService>();

        var sp = services.BuildServiceProvider();
        return (sp, directory, hcmMockHandler, graphMockHandler);
    }

    [Fact]
    public async Task ProductionPipeline_EndToEnd_ReconcilesAllSixHrScenarios_AndMaintainsStrictIdempotency()
    {
        // Arrange
        var (sp, directory, hcmMock, graphMock) = BuildPipelineServiceProvider();
        var useCase = sp.GetRequiredService<ReconcileBatchUseCase>();

        // Act - Pass 1: Initial full reconciliation
        var sw = Stopwatch.StartNew();
        var pass1Report = await useCase.ExecuteAsync();

        // Assert - Pass 1 Functional Verification
        pass1Report.Should().NotBeNull();
        pass1Report.TotalProcessed.Should().Be(6);
        pass1Report.CircuitBreakerTripped.Should().BeFalse();

        // 1. Joiners: 3 users created (EMP-001 Mariana Lima, EMP-004 Mariana Lima 2, EMP-005 José d'Ávila)
        pass1Report.CreatedCount.Should().Be(3);

        // 2. Leaver: 1 account disabled & session revoked (EMP-003 Beatriz Souza)
        pass1Report.DisabledCount.Should().Be(1);
        pass1Report.SessionsRevokedCount.Should().Be(1);

        // 3. Group memberships: 7 added, 2 removed
        // Added: EMP-001 (engineering, all-staff), EMP-002 (finance, all-staff), EMP-004 (all-staff), EMP-005 (engineering, all-staff) = 7
        // Removed: EMP-002 (engineering), EMP-003 (all-staff) = 2
        pass1Report.GroupMembershipsAdded.Should().Be(7);
        pass1Report.GroupMembershipsRemoved.Should().Be(2);

        // 4. Batch payload verification sent to Microsoft Graph API
        directory.BatchRequestsReceived.Should().Be(1);
        directory.CapturedBatchBodies.Should().HaveCount(1);
        var batchBody = directory.CapturedBatchBodies[0];

        // Scenario 1: Joiner Mariana Lima (EMP-001)
        batchBody.Should().Contain("mariana.lima@company.onmicrosoft.com");

        // Scenario 4: Homonym Mariana Lima (EMP-004) resolved with suffix 2
        batchBody.Should().Contain("mariana.lima2@company.onmicrosoft.com");

        // Scenario 5: Diacritics José d'Ávila (EMP-005) sanitized to jose.davila
        batchBody.Should().Contain("jose.davila@company.onmicrosoft.com");

        // Scenario 3: Leaver Beatriz Souza (EMP-003) disabled & sessions revoked
        batchBody.Should().Contain($"/users/{directory.LeaverGraphId}");
        batchBody.Should().Contain($"/users/{directory.LeaverGraphId}/revokeSignInSessions");

        // Scenario 2: Mover Rodrigo Alves (EMP-002) group transitions
        batchBody.Should().Contain($"/groups/{directory.EngineeringGroupId}/members/{directory.MoverGraphId}/$ref");
        batchBody.Should().Contain($"/groups/{directory.FinanceGroupId}/members/$ref");

        // Scenario 6: Idempotent No-Op Felipe Gomes (EMP-006) generated zero mutations for its ID
        batchBody.Should().NotContain(directory.NoopGraphId.ToString());

        // Act - Pass 2: Strict Idempotency execution over unchanged sources
        var pass2Report = await useCase.ExecuteAsync();
        sw.Stop();

        // Assert - Pass 2 Strict Idempotency Verification
        pass2Report.Should().NotBeNull();
        pass2Report.TotalProcessed.Should().Be(6);
        pass2Report.CreatedCount.Should().Be(0);
        pass2Report.UpdatedCount.Should().Be(0);
        pass2Report.EnabledCount.Should().Be(0);
        pass2Report.DisabledCount.Should().Be(0);
        pass2Report.SessionsRevokedCount.Should().Be(0);
        pass2Report.GroupMembershipsAdded.Should().Be(0);
        pass2Report.GroupMembershipsRemoved.Should().Be(0);
        pass2Report.CircuitBreakerTripped.Should().BeFalse();

        // STRICT IDEMPOTENCY: Graph batch endpoint received ZERO new mutation calls in Pass 2
        directory.BatchRequestsReceived.Should().Be(1);
        directory.CapturedBatchBodies.Should().HaveCount(1);

        // Performance: Entire E2E reconciliation lifecycle completes in subseconds (< 1000ms)
        sw.ElapsedMilliseconds.Should().BeLessThan(1000);
    }

    [Fact]
    public async Task ProductionPipeline_DryRunAudit_ResolvesFromDi_AndProducesAccurateReportWithoutMutations()
    {
        // Arrange
        var (sp, directory, _, _) = BuildPipelineServiceProvider();
        var auditUseCase = sp.GetRequiredService<DryRunAuditUseCase>();

        // Act
        var report = await auditUseCase.ExecuteAsync();

        // Assert
        report.Should().NotBeNull();
        report.TotalProcessed.Should().Be(6);
        report.CreatedCount.Should().Be(3);
        report.DisabledCount.Should().Be(1);
        report.SessionsRevokedCount.Should().Be(1);
        report.GroupMembershipsAdded.Should().Be(7);
        report.GroupMembershipsRemoved.Should().Be(2);
        report.CircuitBreakerTripped.Should().BeFalse();

        // Dry-run MUST NOT send any batch mutations to Graph API
        directory.BatchRequestsReceived.Should().Be(0);
        directory.CapturedBatchBodies.Should().BeEmpty();
    }
}
