# Spec de Design: Two-Stage Parallel Batching no Microsoft Graph, Proteção de Paginação e Empacotamento NuGet

- **Data:** 2026-10-07
- **Status:** Aprovado
- **Autor:** Antigravity / Pair Programming
- **Alvo:** `HcmIdentityProvisioning.sln` (.NET 10 / C# 14)

---

## 1. Visão Geral e Motivação

Durante testes de homologação contra o Microsoft Entra ID (Azure AD) real utilizando conectores HCM externos, foram identificados gargalos e limitações fundamentais na integração do Microsoft Graph API `$batch` e no consumo de dados de RH:

1. **Incompatibilidade de Topologia OData no Graph `$batch`:**
   - O endpoint `$batch` do Microsoft Graph rejeita corpos JSON contendo referências relativas (`{ "@odata.id": "$1" }`) para vinculação de membros em grupos (`POST /groups/{id}/members/$ref`), exigindo obrigatoriamente a URI canônica absoluta (`https://graph.microsoft.com/v1.0/directoryObjects/{guid}`).
   - O uso de referências relativas gerava `400 Bad Request` e abortava em cascata todas as demais subrequisições com `424 FailedDependency` e a mensagem: `"Batch should be either fully sequential or fully parallel"`.
2. **Riscos de Ciclos ou Dados Duplicados na Paginação HCM:**
   - Conectores HCM com paginação instável podem reemitir os mesmos colaboradores em páginas subsequentes, gerando reprocessamento redundante ou potenciais loops infinitos.
3. **Consumo Corporativo sem Fork Drift:**
   - A dependência de código-fonte compartilhado ou *forks* privados cria silos de código e dificulta a evolução upstream de correções de bugs. A distribuição corporativa deve ser realizada através de pacotes NuGet independentes (`Domain`, `Application`, `Infrastructure`).

---

## 2. Arquitetura e Componentes Afetados

```
┌────────────────────────────────────────────────────────────────────────┐
│                        Camada de Domínio                               │
│  AddGroupMemberAction(Guid GraphId, Guid GroupId, string GroupName,    │
│                       EmployeeId? EmployeeId = null)                   │
└───────────────────────────────────┬────────────────────────────────────┘
                                    │
┌───────────────────────────────────▼────────────────────────────────────┐
│                       Camada de Aplicação                              │
│  - IdentityReconciliationService: Emite AddGroupMemberAction com ID    │
│  - ReconcileBatchUseCase & DryRunAuditUseCase: Proteção anti-ciclos    │
│    usando seenEmployeeIds por execução                                │
└───────────────────────────────────┬────────────────────────────────────┘
                                    │
┌───────────────────────────────────▼────────────────────────────────────┐
│                     Camada de Infraestrutura                           │
│  EntraIdGraphAdapter: Two-Stage Parallel Batching                      │
│    ├─ Estágio 1: Mutações Principais (Users, Updates, 100% paralelo)   │
│    │  └─ Extrai GUIDs de 201 Created ou resolve 409 Conflict           │
│    └─ Estágio 2: Grupos de Novos Usuários (Canonical URIs, 100% parl.) │
└───────────────────────────────────┬────────────────────────────────────┘
                                    │
┌───────────────────────────────────▼────────────────────────────────────┐
│                        Camada de Apresentação                          │
│  - IConsolePrompter: ReadMaskedPassword com fallback não interativo    │
│  - CLI: Flag global --verbose / -v para diagnósticos LogLevel.Info     │
└────────────────────────────────────────────────────────────────────────┘
```

---

## 3. Especificações Detalhadas

### 3.1. Domínio: Enriquecimento de `AddGroupMemberAction`

- **Arquivo:** `src/HcmIdentityProvisioning.Domain/Actions/DeltaAction.cs`
- **Assinatura:**
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
- **Contrato:**
  - Quando emitido para um Joiner (onde o usuário ainda não possui GUID no diretório), `GraphId` é `Guid.Empty` e `EmployeeId` contém o identificador do colaborador.
  - Para Movers ou reconciliações de usuários existentes, `GraphId` contém o GUID real do usuário e `EmployeeId` pode ser fornecido opcionalmente.
- **Emissão:**
  - Em `IdentityReconciliationService.ReconcileEmployeeAsync`, ao adicionar grupos para Joiners:
    ```csharp
    actions.Add(new AddGroupMemberAction(Guid.Empty, group.Id, group.DisplayName, employee.Id));
    ```

---

### 3.2. Aplicação: Proteção Defensiva contra Ciclos na Paginação

- **Arquivos:**
  - `src/HcmIdentityProvisioning.Application/UseCases/ReconcileBatchUseCase.cs`
  - `src/HcmIdentityProvisioning.Application/UseCases/DryRunAuditUseCase.cs`
- **Comportamento:**
  1. No início do caso de uso, antes do loop `while (hasNext)`:
     ```csharp
     var seenEmployeeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
     ```
  2. Em cada iteração de página:
     ```csharp
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
     ```
  3. O loop de reconciliação itera estritamente sobre `uniqueEmployees`.
  4. A chamada do Circuit Breaker permanece:
     ```csharp
     if (!tripped && _circuitBreaker.ShouldTrip(uniqueEmployees.Count, batchActions, out var reason))
     ```
     Preservando a integridade mesmo quando `uniqueEmployees.Count == 0`.

---

### 3.3. Infraestrutura: Two-Stage Parallel Batching (`EntraIdGraphAdapter`)

- **Arquivo:** `src/HcmIdentityProvisioning.Infrastructure/Graph/EntraIdGraphAdapter.cs`
- **Método:** `ApplyBatchMutationsAsync(IEnumerable<DeltaAction> actions, CancellationToken ct = default)`

#### Fase 1: Triagem de Mutações
O adaptador decompõe a lista recebida em duas coleções:
1. `directActions`: Ações que operam de forma autônoma sem requisições precedentes:
   - `CreateUserAction`
   - `UpdateDisplayNameAction`
   - `EnableAccountAction`
   - `DisableAccountAction`
   - `RevokeSessionsAction`
   - `RemoveGroupMemberAction`
   - `AddGroupMemberAction` (onde `GraphId != Guid.Empty`)
2. `pendingJoinerGroupActions`: `AddGroupMemberAction` onde `GraphId == Guid.Empty`.

#### Fase 2: Execução do Estágio 1 (Mutações Principais - 100% Paralelo)
- Fatiado em lotes de até `MaxBatchSize` (padrão: 20 requisições).
- **Zero propriedades `DependsOn`**: todas as subrequisições rodam em paralelo.
- Mapa de correlação: `var resolvedUserGuids = new Dictionary<EmployeeId, Guid>();`
- Para cada `CreateUserAction create`:
  - **Status `201 Created`:** A subresposta é lida via `batchResponse.GetResponseByIdAsync(stepId)`. O JSON é desserializado e o campo `"id"` é extraído:
    `resolvedUserGuids[create.Employee.Id] = Guid.Parse(id);`
  - **Status `409 Conflict` (Idempotência):** O usuário já existe no diretório. O `create.Employee.Id` é adicionado a uma lista `conflictedEmployeeIds`.
- **Resolução de `409 Conflict`:**
  Se `conflictedEmployeeIds.Count > 0`:
  `var existingUsers = await GetUsersByEmployeeIdsAsync(conflictedEmployeeIds, ct);`
  Para cada usuário existente encontrado:
  `resolvedUserGuids[user.EmployeeId] = user.GraphId;`

#### Fase 3: Execução do Estágio 2 (Grupos de Novos Usuários - 100% Paralelo)
- Para cada item de `pendingJoinerGroupActions`:
  - Se `pending.EmployeeId != null && resolvedUserGuids.TryGetValue(pending.EmployeeId, out var userGuid)`:
    Gera uma nova ação: `new AddGroupMemberAction(userGuid, pending.GroupId, pending.GroupName, pending.EmployeeId)`.
- Se houver ações resolvidas:
  - Fatiar em chunks de até 20 requisições.
  - Subrequisições geradas com URI canônica:
    `@odata.id: $"https://graph.microsoft.com/v1.0/directoryObjects/{addGroup.GraphId}"`
  - **Zero propriedades `DependsOn`**.
  - Subrespostas com `400 Bad Request` ou `409 Conflict` são toleradas idempotentemente (usuário já é membro).

#### Fase 4: Simplificação do Retry
- `RetryThrottledActionsAsync` remove qualquer rastreamento sequencial ou de `lastCreateUserStepId`. O retry de `429 Too Many Requests` é 100% paralelo.

---

### 3.4. CLI: Leitura Mascarada e Diagnósticos Verbose

- **Arquivos:**
  - `src/HcmIdentityProvisioning.Cli/Utils/IConsolePrompter.cs`
  - `src/HcmIdentityProvisioning.Cli/Program.cs`

#### IConsolePrompter.ReadMaskedPassword
- Assinatura: `string ReadMaskedPassword(string prompt);`
- **Modo Interativo (`!IsInputRedirected`):**
  - Exibe o prompt em amarelo.
  - Lê caracteres com `Console.ReadKey(intercept: true)`.
  - Imprime `*` para cada caractere digitado.
  - Suporta `Backspace` removendo o caractere (`\b \b`).
  - Finaliza no `Enter` e quebra linha.
- **Modo Não Interativo (`IsInputRedirected`):**
  - Fallback: `Console.ReadLine()?.Trim() ?? string.Empty`.

#### Flag Global `--verbose` / `-v`
- Registrada como `GlobalOption<bool>` no `RootCommand`.
- Altera o nível do console logger para `LogLevel.Information`, exibindo URLs, métodos HTTP e diagnósticos detalhados.

---

### 3.5. Distribuição NuGet

- **Projetos Empacotados (`<IsPackable>true</IsPackable>` padrão):**
  1. `src/HcmIdentityProvisioning.Domain/HcmIdentityProvisioning.Domain.csproj`
  2. `src/HcmIdentityProvisioning.Application/HcmIdentityProvisioning.Application.csproj`
  3. `src/HcmIdentityProvisioning.Infrastructure/HcmIdentityProvisioning.Infrastructure.csproj`
- **Propriedades Comuns nos `.csproj`:**
  - `<Version>1.1.0</Version>`
  - `<Authors>Hcm Identity Provisioning Team</Authors>`
  - `<Description>Enterprise Microsoft Entra ID SCIM/HCM Provisioning Engine - ...</Description>`
  - `<PackageLicenseExpression>MIT</PackageLicenseExpression>`
  - `<RepositoryType>git</RepositoryType>`
  - `<GeneratePackageOnBuild>false</GeneratePackageOnBuild>`
- **Projetos de Execução (`<IsPackable>false</IsPackable>`):**
  - `src/HcmIdentityProvisioning.Cli/HcmIdentityProvisioning.Cli.csproj`
  - `src/HcmIdentityProvisioning.Functions/HcmIdentityProvisioning.Functions.csproj`

---

## 4. Estratégia de Testes e Validação

1. **`EntraIdGraphAdapterTests.cs`:**
   - Teste de Joiner em 2 estágios consecutivos (Lote 1: `POST /users` retorna 201 com ID; Lote 2: `POST /groups/.../$ref` com `@odata.id` canônico).
   - Teste de resolução idempotente de `409 Conflict` no Estágio 1 com resolução de GUID para o Estágio 2.
2. **`ProductionPipelineIntegrationTests.cs`:**
   - Pass 1: Valida o recebimento de exatamente 2 lotes no mock (Estágio 1: Usuários/Mutações, Estágio 2: Grupos de novos usuários).
   - Pass 2: Valida que a reconciliação subsequente não gera novos lotes (total permanece 2).
3. **Casos de Uso de Reconciliação:**
   - Teste em `ReconcileBatchUseCaseTests.cs` e `DryRunAuditUseCaseTests.cs` simulando conector HCM com dados cíclicos.
4. **CLI Tests:**
   - Testes unitários para `IConsolePrompter.ReadMaskedPassword` e flag `--verbose`.
5. **Verificação de Build e Empacotamento:**
   - `dotnet test` executando com 100% de sucesso em toda a solução.
   - `dotnet pack -c Release` gerando com sucesso os três arquivos `.nupkg`.
