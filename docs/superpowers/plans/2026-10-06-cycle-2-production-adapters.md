# HCM → Microsoft Entra ID Provisioning: Ciclo 2 (Adaptadores de Produção) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implementar os adaptadores de produção para o Microsoft Entra ID (`EntraIdGraphAdapter : IIdentityStore` com Microsoft Graph SDK v5, batching e motor anti-throttling com Fast-Exit) e para sistemas de RH externos (`GenericRestHcmConnector : IHcmConnector` com resiliência Polly v8 e Strategy Pattern extensível `IHcmPayloadMapper`), incluindo composição via DI e suíte completa de testes de integração com `MockHttpMessageHandler`.

**Architecture:** Clean Architecture / Hexagonal Architecture em .NET 10. Implementação de adaptadores de infraestrutura para as portas `IIdentityStore` e `IHcmConnector` definidas no Domínio (Ciclo 1). Desacoplamento estrito entre transporte HTTP resiliente e schemas de terceiros via `IHcmPayloadMapper`. Tratamento de limites do Microsoft Entra ID Free com batching de até 20 itens, particionamento OData em chunks de 15 IDs e política de Fast-Exit em HTTP 429 para otimização de cotas serverless no Azure Functions.

**Tech Stack:** .NET 10 (`net10.0`), C# 14, `Microsoft.Graph` (5.x), `Azure.Identity` (1.13+), `Microsoft.Extensions.Http.Resilience` (10.x), `Microsoft.Extensions.Options` (10.x), xUnit, `FluentAssertions`, `NSubstitute`.

**Spec:** [`docs/superpowers/specs/2026-10-06-cycle-2-production-adapters-design.md`](file:///d:/Projects/hcm-identity-provisioning-automation/docs/superpowers/specs/2026-10-06-cycle-2-production-adapters-design.md)

## Global Constraints

- Target Framework: `net10.0` com C# 14 (`<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`).
- Zero Regressão: Os 123 testes existentes do Ciclo 1 devem continuar 100% verdes após todas as alterações.
- Pure Domain: `HcmIdentityProvisioning.Domain` e `Application` permanecem 100% inalterados e livres de pacotes externos.
- Managed Group Boundary: `EntraIdGraphAdapter` restringe associações estritamente aos grupos com prefixo de governança (padrão: `grp-iam-`).
- Throttling Fast-Exit: Se a Graph API retornar `Retry-After > 5s` ou falha reincidente em 429, o adapter emite aviso estruturado `WARNING_GRAPH_THROTTLE_FAST_EXIT` e encerra a execução atual sem bloquear a thread do Azure Functions.
- Test Determinism: Todos os testes de integração de infraestrutura devem rodar em memória via `MockHttpMessageHandler`, executando em subsegundos sem depender de internet ou credenciais do Azure.

---

### Task 1: Pacotes NuGet & Scaffolding de Infraestrutura do Ciclo 2

**Files:**
- Modify: `src/HcmIdentityProvisioning.Infrastructure/HcmIdentityProvisioning.Infrastructure.csproj`

**Interfaces:**
- Consumes: `HcmIdentityProvisioning.Domain`, `HcmIdentityProvisioning.Application`
- Produces: Referências aos pacotes `Microsoft.Graph`, `Azure.Identity`, `Microsoft.Extensions.Http.Resilience`, `Microsoft.Extensions.Options`.

- [ ] **Step 1: Adicionar pacotes NuGet ao projeto Infrastructure**

Adicionar as seguintes referências no arquivo `src/HcmIdentityProvisioning.Infrastructure/HcmIdentityProvisioning.Infrastructure.csproj`:
```xml
    <PackageReference Include="Microsoft.Graph" Version="5.71.0" />
    <PackageReference Include="Azure.Identity" Version="1.13.2" />
    <PackageReference Include="Microsoft.Extensions.Http.Resilience" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Options" Version="10.0.0" />
```

- [ ] **Step 2: Restaurar e compilar a solução**

Run: `dotnet restore && dotnet build`  
Expected: Build succeeded with 0 errors.

- [ ] **Step 3: Executar testes de regressão**

Run: `dotnet test`  
Expected: 123 passed, 0 failed.

- [ ] **Step 4: Commit**

```bash
git add src/HcmIdentityProvisioning.Infrastructure/HcmIdentityProvisioning.Infrastructure.csproj
git commit -m "build(infrastructure): add Microsoft.Graph, Azure.Identity, and Http.Resilience packages"
```

---

### Task 2: Estratégia de Mapeamento Extensível & Conector REST HCM

**Files:**
- Create: `src/HcmIdentityProvisioning.Infrastructure/Connectors/Rest/IHcmPayloadMapper.cs`
- Create: `src/HcmIdentityProvisioning.Infrastructure/Connectors/Rest/DefaultHcmPayloadMapper.cs`
- Create: `src/HcmIdentityProvisioning.Infrastructure/Connectors/Rest/RestHcmConnectorOptions.cs`
- Create: `src/HcmIdentityProvisioning.Infrastructure/Connectors/Rest/GenericRestHcmConnector.cs`
- Test: `tests/HcmIdentityProvisioning.Infrastructure.Tests/Connectors/Rest/GenericRestHcmConnectorTests.cs`

**Interfaces:**
- Consumes: `IHcmConnector`, `PagedResult<Employee>`, `EmployeeId`, `EmployeeStatus`, `Employee`
- Produces: `IHcmPayloadMapper`, `DefaultHcmPayloadMapper`, `RestHcmConnectorOptions`, `GenericRestHcmConnector`

- [ ] **Step 1: Escrever testes unitários para GenericRestHcmConnector e Mappers**

Criar `tests/HcmIdentityProvisioning.Infrastructure.Tests/Connectors/Rest/GenericRestHcmConnectorTests.cs` testando:
1. Mapeamento padrão (`DefaultHcmPayloadMapper`) com envelope `{ items, pageNumber, pageSize, totalCount }`.
2. Headers de autenticação (Bearer Token e ApiKey).
3. Implementação de mapper customizado para ERP de terceiro.
4. Resiliência HTTP em caso de erro 503 com retry automático.

- [ ] **Step 2: Executar testes para confirmar falha de compilação**

Run: `dotnet test --filter FullyQualifiedName~GenericRestHcmConnectorTests`  
Expected: Falha de compilação (tipos não existem ainda).

- [ ] **Step 3: Implementar IHcmPayloadMapper, DefaultHcmPayloadMapper, RestHcmConnectorOptions e GenericRestHcmConnector**

1. `IHcmPayloadMapper.cs`:
```csharp
namespace HcmIdentityProvisioning.Infrastructure.Connectors.Rest;

using HcmIdentityProvisioning.Domain.Common;
using HcmIdentityProvisioning.Domain.Entities;

public interface IHcmPayloadMapper
{
    string BuildPageUri(string endpoint, int pageNumber, int pageSize);
    Task<PagedResult<Employee>> MapResponseAsync(HttpResponseMessage response, int pageNumber, int pageSize, CancellationToken ct = default);
}
```

2. `RestHcmConnectorOptions.cs`:
```csharp
namespace HcmIdentityProvisioning.Infrastructure.Connectors.Rest;

public sealed class RestHcmConnectorOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string Endpoint { get; set; } = "/api/v1/employees";
    public HcmAuthScheme AuthScheme { get; set; } = HcmAuthScheme.Bearer;
    public string? BearerToken { get; set; }
    public string? ApiKey { get; set; }
    public string ApiKeyHeaderName { get; set; } = "X-Api-Key";
    public int TimeoutSeconds { get; set; } = 30;
    public Dictionary<string, string> CustomHeaders { get; set; } = new();
}

public enum HcmAuthScheme
{
    None,
    Bearer,
    ApiKey,
    Basic
}
```

3. `DefaultHcmPayloadMapper.cs`:
Desserializa JSON padrão `{ items: [...], pageNumber, pageSize, totalCount }`, mapeando `id`, `fullName`, `status` ("Active" -> `EmployeeStatus.Active`), `department`, `jobTitle`, `extendedAttributes`.

4. `GenericRestHcmConnector.cs`:
Implementa `IHcmConnector`, utiliza `HttpClient` e `IHcmPayloadMapper`.

- [ ] **Step 4: Executar testes para verificar aprovação**

Run: `dotnet test --filter FullyQualifiedName~GenericRestHcmConnectorTests`  
Expected: PASS com 100% dos testes do conector verdes.

- [ ] **Step 5: Commit**

```bash
git add src/HcmIdentityProvisioning.Infrastructure/Connectors/Rest tests/HcmIdentityProvisioning.Infrastructure.Tests/Connectors/Rest
git commit -m "feat(infrastructure): implement GenericRestHcmConnector with extensible IHcmPayloadMapper"
```

---

### Task 3: Mock Infrastructure para Microsoft Graph SDK v5

**Files:**
- Create: `tests/HcmIdentityProvisioning.Infrastructure.Tests/Mocks/MockHttpMessageHandler.cs`
- Create: `src/HcmIdentityProvisioning.Infrastructure/Graph/EntraIdGraphOptions.cs`

**Interfaces:**
- Consumes: `System.Net.Http.HttpMessageHandler`, `TokenCredential`
- Produces: `MockHttpMessageHandler`, `EntraIdGraphOptions`

- [ ] **Step 1: Criar EntraIdGraphOptions em Infrastructure**

Criar `src/HcmIdentityProvisioning.Infrastructure/Graph/EntraIdGraphOptions.cs`:
```csharp
namespace HcmIdentityProvisioning.Infrastructure.Graph;

using Azure.Core;

public sealed class EntraIdGraphOptions
{
    public string TenantDomain { get; set; } = string.Empty;
    public string ManagedGroupPrefix { get; set; } = "grp-iam-";
    public int MaxBatchSize { get; set; } = 20;
    public int FilterChunkSize { get; set; } = 15;
    public int MaxImmediateRetryDelaySeconds { get; set; } = 5;
    public TokenCredential? CustomCredential { get; set; }
}
```

- [ ] **Step 2: Criar MockHttpMessageHandler para intercepção HTTP e simulação do Graph SDK**

Criar `tests/HcmIdentityProvisioning.Infrastructure.Tests/Mocks/MockHttpMessageHandler.cs`:
Um manipulador HTTP flexível capaz de registrar rotas e responder com status codes, headers (ex: `Retry-After`) e payloads JSON específicos para rotas `/v1.0/groups`, `/v1.0/users` e `/v1.0/$batch`.

- [ ] **Step 3: Compilar e verificar**

Run: `dotnet build`  
Expected: Build succeeded with 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/HcmIdentityProvisioning.Infrastructure/Graph/EntraIdGraphOptions.cs tests/HcmIdentityProvisioning.Infrastructure.Tests/Mocks/MockHttpMessageHandler.cs
git commit -m "test(infrastructure): add MockHttpMessageHandler and EntraIdGraphOptions"
```

---

### Task 4: EntraIdGraphAdapter - Operações de Leitura (Grupos, UPN e Usuários)

**Files:**
- Create: `src/HcmIdentityProvisioning.Infrastructure/Graph/EntraIdGraphAdapter.cs` (estrutura e métodos de leitura)
- Test: `tests/HcmIdentityProvisioning.Infrastructure.Tests/Graph/EntraIdGraphAdapterTests.cs`

**Interfaces:**
- Consumes: `IIdentityStore`, `EntraIdGraphOptions`, `EmployeeId`, `UserPrincipalName`, `EntraUser`, `ManagedGroup`
- Produces: `EntraIdGraphAdapter.GetManagedGroupsAsync`, `IsUserPrincipalNameAvailableAsync`, `GetUsersByEmployeeIdsAsync`

- [ ] **Step 1: Escrever testes unitários para as operações de leitura do Graph Adapter**

Em `tests/HcmIdentityProvisioning.Infrastructure.Tests/Graph/EntraIdGraphAdapterTests.cs`:
1. `GetManagedGroupsAsync_FiltersOnlyManagedPrefix_CaseInsensitive`: valida retorno e comparação case-insensitive.
2. `IsUserPrincipalNameAvailableAsync_WhenEmpty_ReturnsTrue_WithSelectId`: valida query com `$select=id` e retorno booleano.
3. `IsUserPrincipalNameAvailableAsync_WhenExists_ReturnsFalse`: valida UPN ocupado.
4. `GetUsersByEmployeeIdsAsync_SplitsIntoChunksOf15_AndFiltersManagedGroups`: valida fatiamento em blocos de 15 IDs e filtragem de grupos fora da fronteira `grp-iam-*`.

- [ ] **Step 2: Executar testes para confirmar falha**

Run: `dotnet test --filter FullyQualifiedName~EntraIdGraphAdapterTests`  
Expected: FAIL (métodos não implementados).

- [ ] **Step 3: Implementar métodos de leitura em EntraIdGraphAdapter**

Implementar em `src/HcmIdentityProvisioning.Infrastructure/Graph/EntraIdGraphAdapter.cs`:
- Construtor aceitando `IOptions<EntraIdGraphOptions>`, `ILogger<EntraIdGraphAdapter>` e opcionalmente um `HttpClient` para injeção de testes.
- `GetManagedGroupsAsync`: `GET /groups?$filter=startswith(displayName, '{ManagedGroupPrefix}')&$select=id,displayName`.
- `IsUserPrincipalNameAvailableAsync`: `GET /users?$filter=userPrincipalName eq '{upn}'&$select=id`.
- `GetUsersByEmployeeIdsAsync`: chunking em blocos de 15, `$filter=employeeId in (...)&$select=id,employeeId,userPrincipalName,displayName,accountEnabled`, consulta de membros de grupos e projeção para `EntraUser`.

- [ ] **Step 4: Executar testes de leitura para confirmar aprovação**

Run: `dotnet test --filter FullyQualifiedName~EntraIdGraphAdapterTests`  
Expected: PASS com todos os testes de leitura verdes.

- [ ] **Step 5: Commit**

```bash
git add src/HcmIdentityProvisioning.Infrastructure/Graph/EntraIdGraphAdapter.cs tests/HcmIdentityProvisioning.Infrastructure.Tests/Graph/EntraIdGraphAdapterTests.cs
git commit -m "feat(infrastructure): implement read queries in EntraIdGraphAdapter"
```

---

### Task 5: EntraIdGraphAdapter - Mutações em Lote e Motor de Fast-Exit Throttling

**Files:**
- Modify: `src/HcmIdentityProvisioning.Infrastructure/Graph/EntraIdGraphAdapter.cs` (`ApplyBatchMutationsAsync`)
- Modify: `tests/HcmIdentityProvisioning.Infrastructure.Tests/Graph/EntraIdGraphAdapterTests.cs`

**Interfaces:**
- Consumes: `IEnumerable<DeltaAction>`, `BatchRequestContentCollection`, `EntraIdGraphOptions`
- Produces: `EntraIdGraphAdapter.ApplyBatchMutationsAsync` com resiliência a 429 e política de Fast-Exit.

- [ ] **Step 1: Escrever testes para ApplyBatchMutationsAsync**

Adicionar em `tests/HcmIdentityProvisioning.Infrastructure.Tests/Graph/EntraIdGraphAdapterTests.cs`:
1. `ApplyBatchMutationsAsync_BuildsValidBatchPayload_For7ActionTypes`: valida empacotamento das 7 ações em `$batch`.
2. `ApplyBatchMutationsAsync_OnMicroRetryThrottle_RetriesFailedItem_Successfully`: simula 429 com `Retry-After: 1` e valida micro-retry bem-sucedido.
3. `ApplyBatchMutationsAsync_OnLongThrottle_TriggersFastExit_WithoutBlocking`: simula 429 com `Retry-After: 60`, valida que o adapter emite log `WARNING_GRAPH_THROTTLE_FAST_EXIT` e encerra imediatamente sem travar a thread.
4. `ApplyBatchMutationsAsync_ToleratesIdempotentGroupFailures_400_and_404`: valida tolerância a "already member" (400/409) e "not in group" (404).

- [ ] **Step 2: Executar testes para verificar falha**

Run: `dotnet test --filter FullyQualifiedName~ApplyBatchMutationsAsync`  
Expected: FAIL.

- [ ] **Step 3: Implementar ApplyBatchMutationsAsync e motor de Fast-Exit**

Em `src/HcmIdentityProvisioning.Infrastructure/Graph/EntraIdGraphAdapter.cs`:
- Mapeamento das 7 `DeltaAction` para requisições em lote de até 20 itens via `$batch`.
- Inspeciona cada sub-resposta:
  - 200/201/204: Sucesso.
  - 400/409 em `AddGroupMemberAction`: Idempotência de sucesso.
  - 404 em `RemoveGroupMemberAction`: Idempotência de sucesso.
  - 429: Se `Retry-After <= MaxImmediateRetryDelaySeconds`, executa `Task.Delay` e retenta itens pendentes (máx 1 vez). Se `> MaxImmediateRetryDelaySeconds` ou retentativa falhar, emite log estruturado e dispara Fast-Exit.
  - 5xx/outros: Emite log `ERROR_GRAPH_SUBREQUEST_FAILED`.

- [ ] **Step 4: Executar todos os testes do EntraIdGraphAdapter**

Run: `dotnet test --filter FullyQualifiedName~EntraIdGraphAdapterTests`  
Expected: PASS com 100% dos testes do Graph Adapter verdes.

- [ ] **Step 5: Commit**

```bash
git add src/HcmIdentityProvisioning.Infrastructure/Graph/EntraIdGraphAdapter.cs tests/HcmIdentityProvisioning.Infrastructure.Tests/Graph/EntraIdGraphAdapterTests.cs
git commit -m "feat(infrastructure): implement batch mutations and Fast-Exit throttling in EntraIdGraphAdapter"
```

---

### Task 6: Extensões de Injeção de Dependência (`ServiceCollectionExtensions`)

**Files:**
- Modify: `src/HcmIdentityProvisioning.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Create: `tests/HcmIdentityProvisioning.Infrastructure.Tests/DependencyInjection/ProductionAdaptersDependencyInjectionTests.cs`

**Interfaces:**
- Consumes: `IServiceCollection`, `EntraIdGraphOptions`, `RestHcmConnectorOptions`, `IHcmPayloadMapper`
- Produces: `AddEntraIdGraphAdapter`, `AddGenericRestHcmConnector`, `AddGenericRestHcmConnector<TMapper>`

- [ ] **Step 1: Escrever testes unitários de DI**

Criar `tests/HcmIdentityProvisioning.Infrastructure.Tests/DependencyInjection/ProductionAdaptersDependencyInjectionTests.cs`:
1. Valida resolução de `IIdentityStore` como `EntraIdGraphAdapter`.
2. Valida resolução de `IHcmConnector` como `GenericRestHcmConnector` com mapper padrão.
3. Valida resolução de `IHcmConnector` como `GenericRestHcmConnector` com mapper customizado.

- [ ] **Step 2: Executar testes de DI para verificar falha**

Run: `dotnet test --filter FullyQualifiedName~ProductionAdaptersDependencyInjectionTests`  
Expected: FAIL (métodos não existem).

- [ ] **Step 3: Implementar métodos de extensão em ServiceCollectionExtensions**

Atualizar `src/HcmIdentityProvisioning.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`:
- Adicionar `AddEntraIdGraphAdapter(this IServiceCollection services, Action<EntraIdGraphOptions> configureOptions)`.
- Adicionar `AddGenericRestHcmConnector(this IServiceCollection services, Action<RestHcmConnectorOptions> configureOptions)`.
- Adicionar `AddGenericRestHcmConnector<TMapper>(this IServiceCollection services, Action<RestHcmConnectorOptions> configureOptions) where TMapper : class, IHcmPayloadMapper`.

- [ ] **Step 4: Executar testes de DI para verificar aprovação**

Run: `dotnet test --filter FullyQualifiedName~ProductionAdaptersDependencyInjectionTests`  
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/HcmIdentityProvisioning.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs tests/HcmIdentityProvisioning.Infrastructure.Tests/DependencyInjection/ProductionAdaptersDependencyInjectionTests.cs
git commit -m "feat(infrastructure): add production adapter DI extensions for EntraIdGraphAdapter and GenericRestHcmConnector"
```

---

### Task 7: Teste Integrado E2E do Pipeline de Produção & Atualização do Roadmap

**Files:**
- Create: `tests/HcmIdentityProvisioning.Infrastructure.Tests/EndToEnd/ProductionPipelineIntegrationTests.cs`
- Modify: `docs/superpowers/execution-roadmap.md`

**Interfaces:**
- Consumes: `ReconcileBatchUseCase`, `EntraIdGraphAdapter`, `GenericRestHcmConnector`, `MockHttpMessageHandler`
- Produces: Teste de integração E2E com os 6 cenários de RH utilizando adaptadores reais de produção; atualização formal do roadmap para Ciclo 2 Concluído.

- [ ] **Step 1: Escrever teste de integração de ponta a ponta do pipeline de produção**

Criar `tests/HcmIdentityProvisioning.Infrastructure.Tests/EndToEnd/ProductionPipelineIntegrationTests.cs`:
1. Configura `GenericRestHcmConnector` apontando para mock REST com os 6 cenários de RH.
2. Configura `EntraIdGraphAdapter` apontando para mock Graph com grupos `grp-iam-*` pré-existentes.
3. Executa `ReconcileBatchUseCase` completo.
4. Asserta que as mutações foram empacotadas e executadas com sucesso via Graph SDK v5.
5. Executa uma segunda vez e asserta **idempotência estrita** (zero mutações geradas).

- [ ] **Step 2: Executar toda a suíte de testes da solução**

Run: `dotnet test`  
Expected: ~150+ testes passando em menos de 2 segundos, zero falhas.

- [ ] **Step 3: Atualizar execution-roadmap.md refletindo conclusão do Ciclo 2**

Atualizar `docs/superpowers/execution-roadmap.md`:
- Status do Ciclo 2 alterado para: **Concluído e Validado (~150+ testes)**.
- Status do Ciclo 3 alterado para: **Pronto para Planejamento**.

- [ ] **Step 4: Commit**

```bash
git add tests/HcmIdentityProvisioning.Infrastructure.Tests/EndToEnd/ProductionPipelineIntegrationTests.cs docs/superpowers/execution-roadmap.md
git commit -m "test(integration): add ProductionPipelineIntegrationTests and mark cycle 2 complete in roadmap"
```
