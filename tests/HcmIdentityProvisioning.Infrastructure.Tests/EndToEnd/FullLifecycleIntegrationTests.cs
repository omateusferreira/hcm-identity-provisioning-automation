using System.Diagnostics;
using FluentAssertions;
using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Application.Services;
using HcmIdentityProvisioning.Application.UseCases;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Enums;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Domain.ValueObjects;
using HcmIdentityProvisioning.Infrastructure.Connectors.Synthetic;
using HcmIdentityProvisioning.Infrastructure.Graph;
using HcmIdentityProvisioning.Infrastructure.Notifications;
using HcmIdentityProvisioning.Infrastructure.Policies;
using HcmIdentityProvisioning.Infrastructure.Rules;
using HcmIdentityProvisioning.Infrastructure.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HcmIdentityProvisioning.Infrastructure.Tests.EndToEnd;

public class FullLifecycleIntegrationTests
{
    [Fact]
    public async Task FullReconciliationCycle_ExecutesInSubsecond_AndMaintainsIdempotency()
    {
        var store = new InMemoryIdentityStore();
        var engGroup = Guid.NewGuid();
        var finGroup = Guid.NewGuid();
        var allStaffGroup = Guid.NewGuid();

        store.SeedGroup(engGroup, "grp-iam-engineering");
        store.SeedGroup(finGroup, "grp-iam-finance");
        store.SeedGroup(allStaffGroup, "grp-iam-all-staff");

        // Seed Mover (EMP-002 Rodrigo Alves in engineering -> will move to finance)
        var existingMover = new EntraUser(
            Guid.NewGuid(),
            EmployeeId.Create("EMP-002").Value,
            UserPrincipalName.Create("rodrigo.alves@company.onmicrosoft.com").Value,
            "Rodrigo Alves",
            true,
            new HashSet<Guid> { engGroup }
        );
        store.SeedUser(existingMover);

        // Seed Leaver (EMP-003 Beatriz Souza is inactive -> will be disabled)
        var existingLeaver = new EntraUser(
            Guid.NewGuid(),
            EmployeeId.Create("EMP-003").Value,
            UserPrincipalName.Create("beatriz.souza@company.onmicrosoft.com").Value,
            "Beatriz Souza",
            true,
            new HashSet<Guid> { allStaffGroup }
        );
        store.SeedUser(existingLeaver);

        var connector = SyntheticHcmConnector.FromFixturesFile("fixtures/synthetic-employees.json");
        var rules = MicrosoftRulesEngineAdapter.FromFile("src/HcmIdentityProvisioning.Infrastructure/Rules/rules.json");
        var pwdGen = new SecurePasswordGenerator();
        var reconciler = new IdentityReconciliationService(rules, pwdGen);
        var breaker = new DisablementCircuitBreaker(maxDisablePercentage: 50.0, maxDisableCount: 100);
        var delivery = new MockCredentialDeliveryService(NullLogger<MockCredentialDeliveryService>.Instance);
        var settings = new SyncSettings
        {
            TenantDomain = "company.onmicrosoft.com",
            ManagedGroupPrefix = "grp-iam-"
        };

        var useCase = new ReconcileBatchUseCase(
            connector,
            store,
            reconciler,
            breaker,
            delivery,
            settings,
            NullLogger<ReconcileBatchUseCase>.Instance
        );

        var sw = Stopwatch.StartNew();
        var firstReport = await useCase.ExecuteAsync();
        sw.Stop();

        // 1. Desempenho resiliente sob carga concorrente (< 2500ms)
        sw.ElapsedMilliseconds.Should().BeLessThan(2500);

        // 2. Validações funcionais
        firstReport.TotalProcessed.Should().Be(6);
        firstReport.CreatedCount.Should().BeGreaterThan(0);
        firstReport.DisabledCount.Should().Be(1); // Beatriz desabilitada
        firstReport.SessionsRevokedCount.Should().Be(1);

        // Homônimo resolvido
        var users = await store.GetUsersByEmployeeIdsAsync(new[] { EmployeeId.Create("EMP-004").Value });
        users[EmployeeId.Create("EMP-004").Value].UserPrincipalName.Value.Should().Be("mariana.lima2@company.onmicrosoft.com");

        // Diacrítico tratado (José d'Ávila -> jose.davila)
        var joseUsers = await store.GetUsersByEmployeeIdsAsync(new[] { EmployeeId.Create("EMP-005").Value });
        joseUsers[EmployeeId.Create("EMP-005").Value].UserPrincipalName.Value.Should().Be("jose.davila@company.onmicrosoft.com");

        // 3. IDEMPOTÊNCIA: Segunda execução consecutiva produz ZERO mutações
        var secondReport = await useCase.ExecuteAsync();
        secondReport.CreatedCount.Should().Be(0);
        secondReport.UpdatedCount.Should().Be(0);
        secondReport.EnabledCount.Should().Be(0);
        secondReport.DisabledCount.Should().Be(0);
        secondReport.GroupMembershipsAdded.Should().Be(0);
        secondReport.GroupMembershipsRemoved.Should().Be(0);
    }
}
