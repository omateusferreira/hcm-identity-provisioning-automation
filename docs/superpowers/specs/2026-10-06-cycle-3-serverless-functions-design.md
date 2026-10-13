# Technical Design: Ciclo 3 — Automação Serverless & Observabilidade

- **Projeto:** HCM to Microsoft Entra ID Lifecycle & Provisioning Automation
- **Ciclo:** Ciclo 3 (Automação Serverless, Envio de Credenciais Out-of-Band & Observabilidade)
- **Autor:** IAM & Cloud Security Engineering
- **Data:** 2026-10-06
- **Status:** Approved Architecture
- **Runtime:** .NET 10 (`net10.0`), C# 14 / Azure Functions (.NET Isolated Worker)

---

## 1. Visão Geral & Contexto

O **Ciclo 3** conclui a entrega da solução corporativa de governança de identidades (HCM $\to$ Microsoft Entra ID), empacotando o motor de reconciliação construído no Ciclo 1 e os adaptadores de produção desenvolvidos no Ciclo 2 para execução contínua, agendada e autônoma na nuvem através do **Azure Functions**.

### 1.1 Objetivos do Ciclo 3
1. **Host Serverless (.NET 10 Isolated Worker):** Implementar o projeto `HcmIdentityProvisioning.Functions` configurado com injeção de dependência dos adaptadores de produção.
2. **Gatilho Agendado Periódico (`SyncTimerFunction`):** Executar a reconciliação declarativa via `TimerTrigger` com agendamento CRON externalizado e parametrizável via configurações de ambiente (`%SyncSchedule%`).
3. **Disparos Manuais e Diagnósticos:** Permitir testes e acionamentos sob demanda sem abrir superfícies de ataque ou webhooks, aproveitando nativamente a **Functions Admin API** do Azure Functions (`Code + Test` no Portal ou `POST /admin/functions/SyncTimerFunction`).
4. **Entrega Out-of-Band de Credenciais (`GraphEmailCredentialDeliveryService`):** Implementar `ICredentialDeliveryService` despachando a senha temporária inicial para o destinatário informado no atributo genérico `employee.ExtendedAttributes["email"]` usando a API Microsoft Graph (`POST /users/{sender}/sendMail`).
5. **Shared Mailbox & Menor Privilégio (PoLP):** Operar o envio de e-mails através de uma caixa de correio compartilhada (*shared mailbox*, ex: `no-reply@empresa.com.br`) sem custo de licença do Exchange Online, com permissão restrita via política de segurança corporativa do Exchange Online (*ApplicationAccessPolicy*).
6. **Observabilidade Semântica:** Exportar métricas operacionais e registros estruturados diretamente para o Azure Application Insights (`customDimensions`), emitindo alerta crítico (`CRITICAL_AUDIT_BREAKER_TRIPPED`) caso o Circuit Breaker seja acionado.

---

## 2. Decisões Arquiteturais & Escopo

| Tópico | Decisão Arquitetural | Racional |
| :--- | :--- | :--- |
| **Modelo de Hosting** | Azure Functions .NET 10 Isolated Worker (`net10.0`) | Padrão moderno e oficial da Microsoft para .NET 10, com controle total sobre o pipeline de DI e isolamento de processo. |
| **Gatilhos (Triggers)** | Apenas `TimerTrigger` (`SyncTimerFunction`) | Elimina complexidade e vetores de ataque de webhooks HTTP desnecessários. Disparos pontuais manuais utilizam a Admin API nativa da plataforma. |
| **Agendamento** | Expressão CRON externalizada (`%SyncSchedule%`) | Permite ajustar o intervalo (ex: a cada 30 min `0 */30 * * * *`) em `local.settings.json` ou no Azure App Settings sem recompilar. |
| **Entrega de Credenciais** | Microsoft Graph Mail API (`users/{SenderEmail}/sendMail`) | Reutiliza a infraestrutura de autenticação do Entra ID/Graph já existente, sem custos com serviços adicionais. |
| **Conta de Remetente** | Shared Mailbox (`SenderEmail: "no-reply@..."`) | Isenção de licença Exchange Online; menor privilégio via *Exchange Online ApplicationAccessPolicy*. |
| **Destinatário da Credencial** | Chave `"email"` em `employee.ExtendedAttributes` | Desacoplamento de domínio: a chave genérica `"email"` pode representar o e-mail pessoal do funcionário, de seu gestor ou da TI. |
| **Observabilidade** | Logs semânticos estruturados via `ILogger` | Popula `customDimensions` no Application Insights de forma nativa e sem overhead de dependências adicionais. |
| **Alerta do Circuit Breaker** | Log em nível `Critical` (`CRITICAL_AUDIT_BREAKER_TRIPPED`) | Facilita a criação de alertas no Azure Monitor/Log Analytics para resposta imediata de SecOps/IAM. |

---

## 3. Estrutura de Projetos & Componentes

```text
HcmIdentityProvisioning.sln
│
├── src/
│   ├── HcmIdentityProvisioning.Domain/
│   │   └── Ports/
│   │       └── ICredentialDeliveryService.cs      # Interface do domínio já existente
│   │
│   ├── HcmIdentityProvisioning.Application/
│   │   └── UseCases/
│   │       └── ReconcileBatchUseCase.cs           # Orquestrador da reconciliação já existente
│   │
│   ├── HcmIdentityProvisioning.Infrastructure/
│   │   ├── Notifications/
│   │   │   ├── GraphEmailDeliveryOptions.cs       # [NOVO] Configuração do remetente e templates
│   │   │   ├── GraphEmailCredentialDeliveryService.cs # [NOVO] Implementação concreta via Graph Mail
│   │   │   └── MockCredentialDeliveryService.cs   # Mock offline existente
│   │   └── DependencyInjection/
│   │       └── ServiceCollectionExtensions.cs     # [ATUALIZADO] AddGraphEmailCredentialDeliveryService
│   │
│   └── HcmIdentityProvisioning.Functions/         # [NOVO] Host Serverless .NET 10 Isolated
│       ├── Program.cs                             # Composition Root, DI & App Insights
│       ├── Functions/
│       │   └── SyncTimerFunction.cs               # TimerTrigger periódico
│       ├── host.json                              # Configurações de logging e runtime do Functions
│       ├── local.settings.json                    # Configurações locais para teste e desenvolvimento
│       └── HcmIdentityProvisioning.Functions.csproj
│
└── tests/
    ├── HcmIdentityProvisioning.Infrastructure.Tests/
    │   └── Notifications/
    │       └── GraphEmailCredentialDeliveryServiceTests.cs # [NOVO] Testes do serviço de e-mail
    │
    └── HcmIdentityProvisioning.Functions.Tests/   # [NOVO] Testes do Host e da Function
        ├── Functions/
        │   └── SyncTimerFunctionTests.cs          # Testes unitários do timer e logs
        ├── DependencyInjection/
        │   └── HostCompositionTests.cs            # Validação do container de DI
        └── HcmIdentityProvisioning.Functions.Tests.csproj
```

---

## 4. Design Detalhado dos Componentes

### 4.1 `GraphEmailDeliveryOptions` & `GraphEmailCredentialDeliveryService`

#### Contrato de Configuração
```csharp
namespace HcmIdentityProvisioning.Infrastructure.Notifications;

public sealed class GraphEmailDeliveryOptions
{
    /// <summary>
    /// Endereço da shared mailbox (ex: no-reply@empresa.com.br).
    /// </summary>
    public string SenderEmail { get; set; } = string.Empty;

    /// <summary>
    /// Assunto do e-mail de primeiro acesso.
    /// </summary>
    public string Subject { get; set; } = "Suas credenciais de primeiro acesso - Bem-vindo(a)!";

    /// <summary>
    /// Define se a mensagem deve ser arquivada na pasta de Itens Enviados da shared mailbox (default: false).
    /// </summary>
    public bool SaveToSentItems { get; set; } = false;
}
```

#### Fluxo de Execução do `DeliverInitialCredentialsAsync`:
1. **Resolução de Destinatário:**
   - Inspeciona `employee.ExtendedAttributes`.
   - Busca a chave `"email"` (comparação ordinal sem distinção entre maiúsculas e minúsculas).
   - Se a chave estiver ausente, vazia ou apenas com espaços:
     - Emite log: `LogWarning("CREDENTIAL_DELIVERY_SKIPPED: Employee {EmployeeId} has no out-of-band email configured", employee.Id);`
     - Retorna `Task.CompletedTask` (não interrompe a criação do usuário no Entra ID).
2. **Validação de Remetente:**
   - Verifica se `_options.SenderEmail` foi preenchido. Caso esteja vazio, lança `InvalidOperationException("SenderEmail must be configured in GraphEmailDeliveryOptions")`.
3. **Construção da Mensagem:**
   - Monta a requisição Graph `SendMailPostRequestBody` contendo:
     - `Message.Subject = _options.Subject;`
     - `Message.ToRecipients = [ new Recipient { EmailAddress = new EmailAddress { Address = recipientEmail } } ];`
     - `Message.Body = new ItemBody { ContentType = BodyType.Html, Content = "..." };` contendo `upn.Value`, `temporaryPassword` e link oficial de login.
     - `SaveToSentItems = _options.SaveToSentItems;`
4. **Disparo da Chamada:**
   - Invoca:
     ```csharp
     await _graphClient.Users[_options.SenderEmail]
         .SendMail
         .PostAsync(requestBody, cancellationToken: ct);
     ```
5. **Segurança de Logs:**
   - Emite log de sucesso: `LogInformation("CREDENTIAL_DELIVERY_SUCCESS: Dispatched initial credentials for employee {EmployeeId} ({Upn}) to {RecipientEmail}", employee.Id, upn, recipientEmail);`
   - A senha temporária **nunca** é incluída em qualquer chamada de log ou trace.
   - Trata exceções com captura e log de erro semântico (`CREDENTIAL_DELIVERY_FAILED`).

---

### 4.2 Host Azure Functions (`HcmIdentityProvisioning.Functions`)

#### `Program.cs` (Composition Root de Produção)
```csharp
var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// Application Insights
builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

var configuration = builder.Configuration;

// Bind de configurações
var syncSettings = new SyncSettings();
configuration.GetSection("HcmSync").Bind(syncSettings);

var rulesPath = configuration["RulesEngine:RulesFilePath"] ?? "Rules/rules.json";

// Registro dos serviços do motor
builder.Services.AddHcmProvisioningCore(syncSettings, rulesPath);

// Registro dos adaptadores de produção
builder.Services.AddEntraIdGraphAdapter(options => configuration.GetSection("EntraId").Bind(options));
builder.Services.AddGenericRestHcmConnector(options => configuration.GetSection("HcmRest").Bind(options));
builder.Services.AddGraphEmailCredentialDeliveryService(options => configuration.GetSection("GraphEmail").Bind(options));

builder.Build().Run();
```

#### `SyncTimerFunction.cs`
- Injeta `ReconcileBatchUseCase` e `ILogger<SyncTimerFunction>`.
- Anotado com `[TimerTrigger("%SyncSchedule%")] TimerInfo timerInfo`.
- Executa `await _useCase.ExecuteAsync(cancellationToken);`.
- Loga resumo semântico:
  ```text
  HCM Identity Sync completed. Total: {Total}, Created: {Created}, Updated: {Updated}, 
  Enabled: {Enabled}, Disabled: {Disabled}, Revoked: {Revoked}, 
  GroupsAdded: {GrpAdd}, GroupsRemoved: {GrpRem}
  ```
- Se `report.CircuitBreakerTripped == true`, emite log `LogCritical("CRITICAL_AUDIT_BREAKER_TRIPPED: {Reason}", report.CircuitBreakerMessage)`.

---

## 5. Estratégia de Testes Automatizados

### 5.1 Testes Unitários de Infraestrutura
- `GraphEmailCredentialDeliveryServiceTests`:
  - `DeliverInitialCredentialsAsync_WhenEmailPresent_SendsGraphMailMessage`
  - `DeliverInitialCredentialsAsync_WhenEmailMissing_LogsWarningAndSkips`
  - `DeliverInitialCredentialsAsync_WhenSenderEmailEmpty_ThrowsInvalidOperationException`
  - `DeliverInitialCredentialsAsync_WhenGraphFails_LogsErrorGracefully`

### 5.2 Testes Unitários e de Integração de Functions
- `SyncTimerFunctionTests`:
  - `Run_WhenInvoked_ExecutesReconcileBatchUseCase`
  - `Run_WhenExecutionCompletes_LogsSemanticSummary`
  - `Run_WhenCircuitBreakerTripped_LogsCriticalAlert`
  - `Run_WhenUseCaseThrows_LogsErrorAndReThrows`
- `HostCompositionTests`:
  - `BuildServiceProvider_ResolvesSyncTimerFunctionAndDependenciesCleanly`

---

## 6. Definition of Done (DoD)

- [ ] Todos os novos projetos compilam sem warnings/erros em .NET 10 (`net10.0`).
- [ ] `GraphEmailCredentialDeliveryService` implementado e coberto por testes unitários.
- [ ] `HcmIdentityProvisioning.Functions` configurado como Isolated Worker com `SyncTimerFunction`.
- [ ] Testes unitários para `SyncTimerFunction` e validação do container de DI aprovados.
- [ ] `dotnet test` executando toda a solução com 100% de sucesso (nenhuma regressão nos 164 testes existentes).
- [ ] Documento `execution-roadmap.md` atualizado com o status do Ciclo 3.
