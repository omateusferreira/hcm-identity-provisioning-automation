# Two-Stage Parallel Batching, Proteção de Paginação e Empacotamento NuGet Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Corrigir a topologia do Microsoft Graph `$batch` no `EntraIdGraphAdapter` através de Two-Stage Parallel Batching, adicionar proteção contra ciclos de paginação na camada de aplicação, implementar leitura de senha mascarada e flag `--verbose` na CLI, e configurar os metadados de empacotamento NuGet para as bibliotecas Core.

**Architecture:** A camada de domínio enriquece `AddGroupMemberAction` com `EmployeeId?`. O caso de uso filtra páginas cíclicas usando `seenEmployeeIds`. O `EntraIdGraphAdapter` decompõe as mutações em dois estágios paralelos sem `dependsOn`: Estágio 1 executa mutações principais e extrai os GUIDs de novos usuários (resolvendo `409 Conflict` idempotentemente); Estágio 2 vincula os novos usuários aos grupos usando URIs canônicas completas (`https://graph.microsoft.com/v1.0/directoryObjects/{guid}`). Os projetos de biblioteca são configurados para distribuição NuGet independente (SemVer 1.1.0).

**Tech Stack:** .NET 10, C# 14, Microsoft.Graph SDK v5, System.CommandLine, xUnit, FluentAssertions, NSubstitute.

**Spec:** `docs/superpowers/specs/2026-10-07-graph-batch-two-stage-and-nuget-packaging-design.md`

## Global Constraints

- Runtime: .NET 10 (net10.0), C# 14 com `Nullable` enable e `ImplicitUsings` enable.
- Zero `dependsOn` no Graph `$batch`: Todas as subrequisições em cada lote são 100% paralelas.
- Zero referências relativas OData: URIs de membros sempre canônicas com GUID real (`https://graph.microsoft.com/v1.0/directoryObjects/{guid}`).
- Resolução de `409 Conflict`: Tolerar idempotentemente `409` no Estágio 1 consultando os GUIDs reais via `GetUsersByEmployeeIdsAsync`.
- Zero Password Logging: Senhas temporárias ou lidas da CLI nunca devem ser registradas em logs.
- Empacotamento NuGet: Apenas `Domain`, `Application` e `Infrastructure` geram `.nupkg`. `Cli` e `Functions` devem ter `<IsPackable>false</IsPackable>`.

---

### Task 1: Enriquecimento de `AddGroupMemberAction` no Domínio e Atualização de `IdentityReconciliationService`

**Files:**
- Modify: `src/HcmIdentityProvisioning.Domain/Actions/DeltaAction.cs:49-56`
- Modify: `src/HcmIdentityProvisioning.Application/Services/IdentityReconciliationService.cs:71-82`
- Test: `tests/HcmIdentityProvisioning.Application.Tests/Services/IdentityReconciliationServiceTests.cs`

**Interfaces:**
- Consumes: `EmployeeId` de `HcmIdentityProvisioning.Domain.ValueObjects`.
- Produces: `AddGroupMemberAction(Guid GraphId, Guid GroupId, string GroupName, EmployeeId? EmployeeId = null)`.

- [ ] **Step 1: Escrever teste que valida o preenchimento de `EmployeeId` em `AddGroupMemberAction` para Joiners**

Em `tests/HcmIdentityProvisioning.Application.Tests/Services/IdentityReconciliationServiceTests.cs`:
```csharp
[Fact]
public async Task Reconcile_WhenJoiner_SetsEmployeeIdOnAddGroupMemberAction()
{
    var service = new IdentityReconciliationService(_rulesEngine, _pwdGen);
    var emp = new Employee(
        EmployeeId.Create("EMP-JOINER-1").Value,
        "Maria Santos",
        EmployeeStatus.Active,
        "Tecnologia",
        "Engenheira",
        new Dictionary<string, string>()
    );

    _rulesEngine.EvaluateDesiredGroupsAsync(emp).Returns(new HashSet<string> { "grp-iam-engineering" });

    var groupGuid = Guid.NewGuid();
    var managedGroups = new Dictionary<string, ManagedGroup>(StringComparer.OrdinalIgnoreCase)
    {
        ["grp-iam-engineering"] = new(groupGuid, "grp-iam-engineering")
    };

    var actions = await service.ReconcileEmployeeAsync(emp, null, "company.onmicrosoft.com", managedGroups, (_, _) => Task.FromResult(true));

    var groupAction = actions.OfType<AddGroupMemberAction>().Should().ContainSingle().Subject;
    groupAction.GraphId.Should().Be(Guid.Empty);
    groupAction.GroupId.Should().Be(groupGuid);
    groupAction.EmployeeId.Should().NotBeNull();
    groupAction.EmployeeId!.Value.Should().Be("EMP-JOINER-1");
}
```

- [ ] **Step 2: Executar teste para verificar que falha na compilação/asserção**

Execute: `dotnet test tests/HcmIdentityProvisioning.Application.Tests --filter "Reconcile_WhenJoiner_SetsEmployeeIdOnAddGroupMemberAction"`
Esperado: FAIL (propriedade `EmployeeId` não existe ou não está atribuída).

- [ ] **Step 3: Implementar a alteração no Domínio e na Aplicação**

Em `src/HcmIdentityProvisioning.Domain/Actions/DeltaAction.cs`:
```csharp
public sealed record AddGroupMemberAction(
    Guid GraphId,
    Guid GroupId,
    string GroupName,
    EmployeeId? EmployeeId = null
) : DeltaAction(GraphId)
{
    public override string ActionName => "AddGroupMember";
}
```

Em `src/HcmIdentityProvisioning.Application/Services/IdentityReconciliationService.cs` (no fluxo Joiner):
```csharp
foreach (var groupName in desiredGroups)
{
    if (caseInsensitiveManagedGroups.TryGetValue(groupName, out var group))
    {
        actions.Add(new AddGroupMemberAction(Guid.Empty, group.Id, group.DisplayName, employee.Id));
    }
    else
    {
        onWarning?.Invoke($"WARNING_MANAGED_GROUP_NOT_FOUND: Group '{groupName}' was not found in directory.");
    }
}
```

- [ ] **Step 4: Executar testes para verificar que passam**

Execute: `dotnet test tests/HcmIdentityProvisioning.Domain.Tests tests/HcmIdentityProvisioning.Application.Tests`
Esperado: PASS (todos os testes de Domain e Application passando).

- [ ] **Step 5: Commit**

```bash
git add src/HcmIdentityProvisioning.Domain/Actions/DeltaAction.cs src/HcmIdentityProvisioning.Application/Services/IdentityReconciliationService.cs tests/HcmIdentityProvisioning.Application.Tests/Services/IdentityReconciliationServiceTests.cs
git commit -m "feat(domain): add optional EmployeeId to AddGroupMemberAction and emit on joiner workflow"
```

---

### Task 2: Proteção Anti-Ciclos na Paginação dos Casos de Uso (`ReconcileBatchUseCase` e `DryRunAuditUseCase`)

**Files:**
- Modify: `src/HcmIdentityProvisioning.Application/UseCases/ReconcileBatchUseCase.cs:40-75`
- Modify: `src/HcmIdentityProvisioning.Application/UseCases/DryRunAuditUseCase.cs:36-70`
- Test: `tests/HcmIdentityProvisioning.Application.Tests/UseCases/ReconcileBatchUseCaseTests.cs`
- Test: `tests/HcmIdentityProvisioning.Application.Tests/UseCases/DryRunAuditUseCaseTests.cs`

**Interfaces:**
- Consumes: `IHcmConnector`, `Employee.Id`.
- Produces: `SyncReport` resiliente a ciclos de paginação.

- [ ] **Step 1: Escrever teste de ciclo de paginação em `ReconcileBatchUseCaseTests`**

Em `tests/HcmIdentityProvisioning.Application.Tests/UseCases/ReconcileBatchUseCaseTests.cs`:
```csharp
[Fact]
public async Task ExecuteAsync_WhenConnectorRepeatsEmployeesInSubsequentPage_HaltsPaginationToPreventCycle()
{
    var connector = Substitute.For<IHcmConnector>();
    var emp = new Employee(EmployeeId.Create("EMP-CYCLE-1").Value, "Carlos Repetido", EmployeeStatus.Active, "TI", "Dev", new Dictionary<string, string>());

    // Página 1 retorna emp com HasNextPage = true
    connector.GetEmployeesPageAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
        .Returns(new PagedResult<Employee>([emp], 1, 50, 2, true));

    // Página 2 retorna o mesmo emp com HasNextPage = true (potencial loop infinito)
    connector.GetEmployeesPageAsync(2, Arg.Any<int>(), Arg.Any<CancellationToken>())
        .Returns(new PagedResult<Employee>([emp], 2, 50, 2, true));

    var store = Substitute.For<IIdentityStore>();
    store.GetManagedGroupsAsync(Arg.Any<CancellationToken>())
        .Returns(new Dictionary<string, ManagedGroup>());
    store.GetUsersByEmployeeIdsAsync(Arg.Any<IEnumerable<EmployeeId>>(), Arg.Any<CancellationToken>())
        .Returns(new Dictionary<EmployeeId, EntraUser>());
    store.IsUserPrincipalNameAvailableAsync(Arg.Any<UserPrincipalName>(), Arg.Any<CancellationToken>())
        .Returns(true);

    var rulesEngine = Substitute.For<IRulesEngine>();
    rulesEngine.EvaluateDesiredGroupsAsync(Arg.Any<Employee>(), Arg.Any<CancellationToken>())
        .Returns(new HashSet<string>());

    var pwdGen = Substitute.For<ISecurePasswordGenerator>();
    pwdGen.GeneratePassword(Arg.Any<int>()).Returns("TestPass123!");

    var circuitBreaker = Substitute.For<ICircuitBreaker>();
    var delivery = Substitute.For<ICredentialDeliveryService>();
    var settings = new SyncSettings { TenantDomain = "contoso.com", BatchPageSize = 50 };

    var useCase = new ReconcileBatchUseCase(
        connector, store, new IdentityReconciliationService(rulesEngine, pwdGen),
        circuitBreaker, delivery, settings, NullLogger<ReconcileBatchUseCase>.Instance);

    // Act
    var report = await useCase.ExecuteAsync();

    // Assert: emp só deve ser processado 1 vez
    report.TotalProcessed.Should().Be(2); // contagem total bruta de itens recebidos
    report.CreatedCount.Should().Be(1);   // apenas 1 criação executada
    await connector.Received(2).GetEmployeesPageAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    await connector.DidNotReceive().GetEmployeesPageAsync(3, Arg.Any<int>(), Arg.Any<CancellationToken>());
}
```

- [ ] **Step 2: Executar teste para verificar que falha**

Execute: `dotnet test tests/HcmIdentityProvisioning.Application.Tests --filter "ExecuteAsync_WhenConnectorRepeatsEmployeesInSubsequentPage_HaltsPaginationToPreventCycle"`
Esperado: FAIL (continua chamando páginas indefinidamente ou cria 2 usuários).

- [ ] **Step 3: Implementar a proteção anti-ciclo em `ReconcileBatchUseCase` e `DryRunAuditUseCase`**

Em `src/HcmIdentityProvisioning.Application/UseCases/ReconcileBatchUseCase.cs`:
```csharp
public async Task<SyncReport> ExecuteAsync(CancellationToken ct = default)
{
    int page = 1;
    bool hasNext = true;
    var warnings = new List<string>();
    var seenEmployeeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    int total = 0, created = 0, updated = 0, enabled = 0, disabled = 0, revoked = 0, grpAdded = 0, grpRemoved = 0;
    bool tripped = false;
    string? breakerMsg = null;

    var managedGroups = await _identityStore.GetManagedGroupsAsync(ct);
    var allocatedUpns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    while (hasNext)
    {
        var paged = await _connector.GetEmployeesPageAsync(page, _settings.BatchPageSize, ct);
        total += paged.Items.Count;

        var uniqueEmployees = paged.Items
            .Where(emp => seenEmployeeIds.Add(emp.Id.Value))
            .ToList();

        if (uniqueEmployees.Count == 0 && paged.Items.Count > 0)
        {
            _logger.LogWarning(
                "Potential cycle detected in HCM connector pagination: all {Count} employees on page {Page} were previously seen. Halting pagination.",
                paged.Items.Count, page);
            break;
        }

        var existingUsers = await _identityStore.GetUsersByEmployeeIdsAsync(uniqueEmployees.Select(e => e.Id), ct);
        var batchActions = new List<DeltaAction>();

        foreach (var emp in uniqueEmployees)
        {
            existingUsers.TryGetValue(emp.Id, out var existing);
            var actions = await _reconciler.ReconcileEmployeeAsync(
                emp,
                existing,
                _settings.TenantDomain,
                managedGroups,
                async (upn, cToken) => !allocatedUpns.Contains(upn.Value) && await _identityStore.IsUserPrincipalNameAvailableAsync(upn, cToken),
                w => warnings.Add(w),
                ct
            );

            foreach (var action in actions)
            {
                if (action is CreateUserAction create)
                {
                    allocatedUpns.Add(create.UserPrincipalName.Value);
                }
            }

            batchActions.AddRange(actions);
        }

        if (!tripped && _circuitBreaker.ShouldTrip(uniqueEmployees.Count, batchActions, out var reason))
        {
            tripped = true;
            breakerMsg = reason;
            _logger.LogCritical("CIRCUIT BREAKER TRIPPED: {Reason}", reason);
        }
        else if (tripped)
        {
            _logger.LogWarning("CIRCUIT BREAKER: Suppressing destructive actions on page {Page} due to prior trip", page);
        }

        if (tripped)
        {
            if (_settings.HaltAllOperationsOnTrip)
            {
                break;
            }

            batchActions.RemoveAll(a => a is DisableAccountAction or RevokeSessionsAction);
        }

        if (batchActions.Count > 0)
        {
            await _identityStore.ApplyBatchMutationsAsync(batchActions, ct);
            // ... contadores
        }

        hasNext = paged.HasNextPage;
        page++;
    }
    // ...
}
```

Aplicar a mesma lógica defensiva de `seenEmployeeIds` e `uniqueEmployees` em `src/HcmIdentityProvisioning.Application/UseCases/DryRunAuditUseCase.cs`.

- [ ] **Step 4: Executar testes de Application para verificar que passam**

Execute: `dotnet test tests/HcmIdentityProvisioning.Application.Tests`
Esperado: PASS (todos os 34+ testes passam).

- [ ] **Step 5: Commit**

```bash
git add src/HcmIdentityProvisioning.Application/UseCases/ReconcileBatchUseCase.cs src/HcmIdentityProvisioning.Application/UseCases/DryRunAuditUseCase.cs tests/HcmIdentityProvisioning.Application.Tests/UseCases/ReconcileBatchUseCaseTests.cs
git commit -m "feat(application): add pagination cycle protection to reconcile and audit use cases"
```

---

### Task 3: Refatoração do `EntraIdGraphAdapter` com Two-Stage Parallel Batching e Resolução de GUIDs

**Files:**
- Modify: `src/HcmIdentityProvisioning.Infrastructure/Graph/EntraIdGraphAdapter.cs:306-600`
- Test: `tests/HcmIdentityProvisioning.Infrastructure.Tests/Graph/EntraIdGraphAdapterTests.cs`

**Interfaces:**
- Consumes: `IEnumerable<DeltaAction>`, `CreateUserAction`, `AddGroupMemberAction(..., EmployeeId)`.
- Produces: Execução em `$batch` 100% paralela com URIs canônicas completas.

- [ ] **Step 1: Escrever teste de dois lotes paralelos no `EntraIdGraphAdapterTests`**

Em `tests/HcmIdentityProvisioning.Infrastructure.Tests/Graph/EntraIdGraphAdapterTests.cs`:
```csharp
[Fact]
public async Task ApplyBatchMutationsAsync_WhenJoinerWithGroups_ExecutesInTwoSequentialParallelBatchesWithoutDependsOn()
{
    // Arrange: 1 Joiner com 2 grupos
    var empId = EmployeeId.Create("EMP-JOINER-1").Value;
    var emp = new Employee(empId, "Alice Santos", EmployeeStatus.Active, "Engenharia", "Dev", new Dictionary<string, string>());
    var upn = UserPrincipalName.Create("alice.santos@contoso.com").Value;
    var createAction = new CreateUserAction(emp, upn, "TempPass123!");

    var group1Id = Guid.NewGuid();
    var group2Id = Guid.NewGuid();
    var addGroup1 = new AddGroupMemberAction(Guid.Empty, group1Id, "grp-iam-eng", empId);
    var addGroup2 = new AddGroupMemberAction(Guid.Empty, group2Id, "grp-iam-all", empId);

    var createdUserGuid = Guid.NewGuid();
    var capturedBatchBodies = new List<string>();

    _mockHandler.WhenMethodAndUrl(HttpMethod.Post, "/v1.0/$batch")
        .RespondWith(async req =>
        {
            var content = await req.Content!.ReadAsStringAsync();
            capturedBatchBodies.Add(content);

            if (content.Contains("POST /users"))
            {
                // Resposta do Estágio 1 (Criar usuário)
                return MockHttpMessageHandler.CreateJsonResponse(MockHttpMessageHandler.CreateBatchResponseBody([
                    new { id = "1", status = 201, body = new { id = createdUserGuid.ToString(), userPrincipalName = upn.Value } }
                ]));
            }

            // Resposta do Estágio 2 (Membros de grupos)
            return MockHttpMessageHandler.CreateJsonResponse(MockHttpMessageHandler.CreateBatchResponseBody([
                new { id = "1", status = 204 },
                new { id = "2", status = 204 }
            ]));
        });

    // Act
    await _sut.ApplyBatchMutationsAsync([createAction, addGroup1, addGroup2]);

    // Assert
    capturedBatchBodies.Should().HaveCount(2);

    // Lote 1: Apenas POST /users, sem dependsOn
    capturedBatchBodies[0].Should().Contain("POST /users");
    capturedBatchBodies[0].Should().NotContain("dependsOn");
    capturedBatchBodies[0].Should().NotContain("/members/$ref");

    // Lote 2: Apenas POST /groups/.../members/$ref com URI canônica e sem dependsOn
    capturedBatchBodies[1].Should().Contain($"/groups/{group1Id}/members/$ref");
    capturedBatchBodies[1].Should().Contain($"/groups/{group2Id}/members/$ref");
    capturedBatchBodies[1].Should().Contain($"https://graph.microsoft.com/v1.0/directoryObjects/{createdUserGuid}");
    capturedBatchBodies[1].Should().NotContain("dependsOn");
    capturedBatchBodies[1].Should().NotContain("$1");
}
```

- [ ] **Step 2: Executar teste para verificar que falha**

Execute: `dotnet test tests/HcmIdentityProvisioning.Infrastructure.Tests --filter "ApplyBatchMutationsAsync_WhenJoinerWithGroups_ExecutesInTwoSequentialParallelBatchesWithoutDependsOn"`
Esperado: FAIL (executa em 1 lote, usa `$1` e `dependsOn`).

- [ ] **Step 3: Implementar Two-Stage Parallel Batching no `EntraIdGraphAdapter`**

Refatorar `src/HcmIdentityProvisioning.Infrastructure/Graph/EntraIdGraphAdapter.cs`:
1. No método `ApplyBatchMutationsAsync`:
   ```csharp
   public async Task ApplyBatchMutationsAsync(IEnumerable<DeltaAction> actions, CancellationToken ct = default)
   {
       ArgumentNullException.ThrowIfNull(actions);
       var actionList = actions.ToList();
       if (actionList.Count == 0) return;

       var directActions = new List<DeltaAction>();
       var pendingJoinerGroupActions = new List<AddGroupMemberAction>();

       foreach (var action in actionList)
       {
           if (action is AddGroupMemberAction addGroup && addGroup.GraphId == Guid.Empty)
           {
               pendingJoinerGroupActions.Add(addGroup);
           }
           else
           {
               directActions.Add(action);
           }
       }

       var resolvedUserGuids = new Dictionary<EmployeeId, Guid>();

       // --- ESTÁGIO 1: Mutações Principais (100% Paralelo) ---
       if (directActions.Count > 0)
       {
           var batchSize = _options.MaxBatchSize > 0 ? _options.MaxBatchSize : 20;
           foreach (var chunk in directActions.Chunk(batchSize))
           {
               var (shouldFastExit, createdGuids, conflictedEmployeeIds) = await ProcessDirectBatchChunkAsync(chunk, ct);
               if (shouldFastExit) return;

               foreach (var (empId, guid) in createdGuids)
               {
                   resolvedUserGuids[empId] = guid;
               }

               if (conflictedEmployeeIds.Count > 0)
               {
                   var existingUsers = await GetUsersByEmployeeIdsAsync(conflictedEmployeeIds, ct);
                   foreach (var (empId, user) in existingUsers)
                   {
                       resolvedUserGuids[empId] = user.GraphId;
                   }
               }
           }
       }

       // --- ESTÁGIO 2: Grupos de Novos Usuários (100% Paralelo) ---
       if (pendingJoinerGroupActions.Count > 0)
       {
           var resolvedGroupActions = new List<AddGroupMemberAction>();
           foreach (var pending in pendingJoinerGroupActions)
           {
               if (pending.EmployeeId != null && resolvedUserGuids.TryGetValue(pending.EmployeeId, out var userGuid))
               {
                   resolvedGroupActions.Add(new AddGroupMemberAction(userGuid, pending.GroupId, pending.GroupName, pending.EmployeeId));
               }
               else
               {
                   _logger.LogWarning(
                       "Unable to resolve GraphId for pending group membership in group {GroupId} ({GroupName}). Action skipped.",
                       pending.GroupId, pending.GroupName);
               }
           }

           if (resolvedGroupActions.Count > 0)
           {
               var batchSize = _options.MaxBatchSize > 0 ? _options.MaxBatchSize : 20;
               foreach (var chunk in resolvedGroupActions.Chunk(batchSize))
               {
                   var shouldFastExit = await ProcessGroupBatchChunkAsync(chunk, ct);
                   if (shouldFastExit) return;
               }
           }
       }
   }
   ```
2. Implementar `ProcessDirectBatchChunkAsync` e `ProcessGroupBatchChunkAsync` sem nenhum `DependsOn`.
3. Ajustar `CreateRequestInformationForAction` para gerar sempre a URI canônica `@odata.id: https://graph.microsoft.com/v1.0/directoryObjects/{addGroup.GraphId}`.
4. Simplificar `RetryThrottledActionsAsync` para processar sem encadeamento nem `lastCreateUserStepId`.

- [ ] **Step 4: Executar testes de infraestrutura para verificar que passam**

Execute: `dotnet test tests/HcmIdentityProvisioning.Infrastructure.Tests --filter "EntraIdGraphAdapterTests"`
Esperado: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/HcmIdentityProvisioning.Infrastructure/Graph/EntraIdGraphAdapter.cs tests/HcmIdentityProvisioning.Infrastructure.Tests/Graph/EntraIdGraphAdapterTests.cs
git commit -m "feat(infrastructure): implement two-stage parallel batching and canonical group refs in EntraIdGraphAdapter"
```

---

### Task 4: Atualização dos Testes de Integração Ponta a Ponta (E2E Pipeline)

**Files:**
- Modify: `tests/HcmIdentityProvisioning.Infrastructure.Tests/EndToEnd/ProductionPipelineIntegrationTests.cs:440-485`

**Interfaces:**
- Consumes: Pipeline de reconciliação completo.
- Produces: Asserções alinhadas com o modelo de dois lotes paralelos.

- [ ] **Step 1: Ajustar a contagem de lotes em `ProductionPipelineIntegrationTests.cs`**

Em `tests/HcmIdentityProvisioning.Infrastructure.Tests/EndToEnd/ProductionPipelineIntegrationTests.cs`:
1. Na primeira passagem (`Pass 1`), verificar que exatamente 2 lotes são capturados:
   ```csharp
   directory.CapturedBatchBodies.Should().HaveCount(2);
   var stage1BatchBody = directory.CapturedBatchBodies[0];
   var stage2BatchBody = directory.CapturedBatchBodies[1];

   // Lote 1 contém criações e mutações de perfil/sessão
   stage1BatchBody.Should().Contain("mariana.lima@company.onmicrosoft.com");
   stage1BatchBody.Should().Contain($"/users/{directory.LeaverGraphId}/revokeSignInSessions");

   // Lote 2 contém associações de grupos com URIs canônicas completas
   stage2BatchBody.Should().Contain($"/groups/{directory.EngineeringGroupId}/members/$ref");
   stage2BatchBody.Should().NotContain("$1");
   stage2BatchBody.Should().NotContain("dependsOn");
   ```
2. Na segunda passagem (`Pass 2` - Idempotência estrita):
   ```csharp
   // STRICT IDEMPOTENCY: Graph batch endpoint received ZERO new mutation calls in Pass 2
   directory.BatchRequestsReceived.Should().Be(2);
   directory.CapturedBatchBodies.Should().HaveCount(2);
   ```

- [ ] **Step 2: Executar testes E2E para verificar aprovação**

Execute: `dotnet test tests/HcmIdentityProvisioning.Infrastructure.Tests --filter "ProductionPipelineIntegrationTests"`
Esperado: PASS (todos os testes de integração passando).

- [ ] **Step 3: Commit**

```bash
git add tests/HcmIdentityProvisioning.Infrastructure.Tests/EndToEnd/ProductionPipelineIntegrationTests.cs
git commit -m "test(integration): update E2E pipeline tests for two-stage parallel batching"
```

---

### Task 5: Melhorias na CLI (`IConsolePrompter.ReadMaskedPassword` e `--verbose`)

**Files:**
- Modify: `src/HcmIdentityProvisioning.Cli/Utils/IConsolePrompter.cs`
- Modify: `src/HcmIdentityProvisioning.Cli/Program.cs`
- Test: `tests/HcmIdentityProvisioning.Cli.Tests/ConsolePrompterTests.cs`

**Interfaces:**
- Consumes: `IConsolePrompter`, `LogLevel`.
- Produces: Leitura de senha mascarada com `*` e flag `--verbose` / `-v`.

- [ ] **Step 1: Escrever teste para `ReadMaskedPassword` no `Cli.Tests`**

Criar `tests/HcmIdentityProvisioning.Cli.Tests/ConsolePrompterTests.cs`:
```csharp
using FluentAssertions;
using HcmIdentityProvisioning.Cli.Utils;
using Xunit;

namespace HcmIdentityProvisioning.Cli.Tests;

public class ConsolePrompterTests
{
    [Fact]
    public void ReadMaskedPassword_WhenInputRedirected_ReturnsLineFromStandardInput()
    {
        var prompter = new ConsolePrompter();
        // Em xUnit runner, Console.IsInputRedirected é true
        prompter.IsInputRedirected.Should().BeTrue();

        using var stringReader = new StringReader("MySecretPassword123\n");
        Console.SetIn(stringReader);

        var result = prompter.ReadMaskedPassword("Enter password: ");

        result.Should().Be("MySecretPassword123");
    }
}
```

- [ ] **Step 2: Executar teste para verificar que falha na compilação**

Execute: `dotnet test tests/HcmIdentityProvisioning.Cli.Tests --filter "ConsolePrompterTests"`
Esperado: FAIL (método `ReadMaskedPassword` não definido).

- [ ] **Step 3: Implementar `ReadMaskedPassword` e flag `--verbose`**

Em `src/HcmIdentityProvisioning.Cli/Utils/IConsolePrompter.cs`:
```csharp
public interface IConsolePrompter
{
    bool IsInputRedirected { get; }
    bool Confirm(string message);
    string ReadMaskedPassword(string prompt);
}

public sealed class ConsolePrompter : IConsolePrompter
{
    public bool IsInputRedirected => Console.IsInputRedirected;

    public bool Confirm(string message)
    {
        if (IsInputRedirected) return false;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write($"{message} [y/N]: ");
        Console.ResetColor();
        var input = Console.ReadLine()?.Trim().ToLowerInvariant();
        return input is "y" or "yes" or "s" or "sim";
    }

    public string ReadMaskedPassword(string prompt)
    {
        if (IsInputRedirected)
        {
            return Console.ReadLine()?.Trim() ?? string.Empty;
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write(prompt);
        Console.ResetColor();

        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Remove(sb.Length - 1, 1);
                    Console.Write("\b \b");
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                sb.Append(key.KeyChar);
                Console.Write("*");
            }
        }

        return sb.ToString();
    }
}
```

Em `src/HcmIdentityProvisioning.Cli/Program.cs`:
1. Detectar `--verbose` / `-v` nos argumentos:
   ```csharp
   var isVerbose = args.Contains("--verbose", StringComparer.OrdinalIgnoreCase) || args.Contains("-v", StringComparer.OrdinalIgnoreCase);
   ```
2. Ajustar nível mínimo do logger do console:
   ```csharp
   builder.AddConsole(cOptions =>
   {
       cOptions.LogToStandardErrorThreshold = LogLevel.Warning;
   }).SetMinimumLevel(isVerbose ? LogLevel.Information : LogLevel.Warning);
   ```
3. Adicionar `verboseOption` global no `RootCommand`:
   ```csharp
   var verboseOption = new Option<bool>(
       aliases: ["--verbose", "-v"],
       description: "Enable verbose diagnostic logging (LogLevel.Information).");
   rootCommand.AddGlobalOption(verboseOption);
   ```

- [ ] **Step 4: Executar testes de CLI para verificar que passam**

Execute: `dotnet test tests/HcmIdentityProvisioning.Cli.Tests`
Esperado: PASS (todos os 22 testes passam).

- [ ] **Step 5: Commit**

```bash
git add src/HcmIdentityProvisioning.Cli/Utils/IConsolePrompter.cs src/HcmIdentityProvisioning.Cli/Program.cs tests/HcmIdentityProvisioning.Cli.Tests/ConsolePrompterTests.cs
git commit -m "feat(cli): add ReadMaskedPassword prompter method and global --verbose flag"
```

---

### Task 6: Configuração de Metadados NuGet e Validação de Pacotes

**Files:**
- Modify: `src/HcmIdentityProvisioning.Domain/HcmIdentityProvisioning.Domain.csproj`
- Modify: `src/HcmIdentityProvisioning.Application/HcmIdentityProvisioning.Application.csproj`
- Modify: `src/HcmIdentityProvisioning.Infrastructure/HcmIdentityProvisioning.Infrastructure.csproj`
- Modify: `src/HcmIdentityProvisioning.Functions/HcmIdentityProvisioning.Functions.csproj`
- Modify: `src/HcmIdentityProvisioning.Cli/HcmIdentityProvisioning.Cli.csproj`

**Interfaces:**
- Produces: Três pacotes `.nupkg` na versão `1.1.0`.

- [ ] **Step 1: Adicionar propriedades NuGet aos `.csproj` das bibliotecas do Core**

Em `src/HcmIdentityProvisioning.Domain/HcmIdentityProvisioning.Domain.csproj`:
```xml
  <PropertyGroup>
    <PackageId>HcmIdentityProvisioning.Domain</PackageId>
    <Version>1.1.0</Version>
    <Authors>Hcm Identity Provisioning Team</Authors>
    <Description>Enterprise Microsoft Entra ID SCIM/HCM Provisioning Engine - Domain Layer</Description>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <RepositoryType>git</RepositoryType>
    <GeneratePackageOnBuild>false</GeneratePackageOnBuild>
  </PropertyGroup>
```

Em `src/HcmIdentityProvisioning.Application/HcmIdentityProvisioning.Application.csproj`:
```xml
  <PropertyGroup>
    <PackageId>HcmIdentityProvisioning.Application</PackageId>
    <Version>1.1.0</Version>
    <Authors>Hcm Identity Provisioning Team</Authors>
    <Description>Enterprise Microsoft Entra ID SCIM/HCM Provisioning Engine - Application Orchestration Layer</Description>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <RepositoryType>git</RepositoryType>
    <GeneratePackageOnBuild>false</GeneratePackageOnBuild>
  </PropertyGroup>
```

Em `src/HcmIdentityProvisioning.Infrastructure/HcmIdentityProvisioning.Infrastructure.csproj`:
```xml
  <PropertyGroup>
    <PackageId>HcmIdentityProvisioning.Infrastructure</PackageId>
    <Version>1.1.0</Version>
    <Authors>Hcm Identity Provisioning Team</Authors>
    <Description>Enterprise Microsoft Entra ID SCIM/HCM Provisioning Engine - Infrastructure Adapters Layer</Description>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <RepositoryType>git</RepositoryType>
    <GeneratePackageOnBuild>false</GeneratePackageOnBuild>
  </PropertyGroup>
```

Em `src/HcmIdentityProvisioning.Functions/HcmIdentityProvisioning.Functions.csproj` e `src/HcmIdentityProvisioning.Cli/HcmIdentityProvisioning.Cli.csproj`:
```xml
  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
```

- [ ] **Step 2: Executar `dotnet pack` e verificar os arquivos `.nupkg` gerados**

Execute: `dotnet pack -c Release`
Esperado: 3 pacotes gerados com sucesso:
- `src/HcmIdentityProvisioning.Domain/bin/Release/HcmIdentityProvisioning.Domain.1.1.0.nupkg`
- `src/HcmIdentityProvisioning.Application/bin/Release/HcmIdentityProvisioning.Application.1.1.0.nupkg`
- `src/HcmIdentityProvisioning.Infrastructure/bin/Release/HcmIdentityProvisioning.Infrastructure.1.1.0.nupkg`

- [ ] **Step 3: Executar a suíte de testes completa da solução**

Execute: `dotnet test`
Esperado: 100% de aprovação em todos os projetos de teste da solução.

- [ ] **Step 4: Commit**

```bash
git add src/HcmIdentityProvisioning.Domain/HcmIdentityProvisioning.Domain.csproj src/HcmIdentityProvisioning.Application/HcmIdentityProvisioning.Application.csproj src/HcmIdentityProvisioning.Infrastructure/HcmIdentityProvisioning.Infrastructure.csproj src/HcmIdentityProvisioning.Functions/HcmIdentityProvisioning.Functions.csproj src/HcmIdentityProvisioning.Cli/HcmIdentityProvisioning.Cli.csproj
git commit -m "chore(nuget): configure package metadata for Domain, Application, and Infrastructure libraries"
```
