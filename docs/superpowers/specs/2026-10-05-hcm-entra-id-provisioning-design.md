# Technical Design: HCM → Microsoft Entra ID Provisioning & Synchronization

- **Author:** Security Engineering & IAM
- **Date:** 2026-10-05
- **Status:** Approved Architecture
- **Target Runtime:** .NET 10 (`net10.0`), C# 14 / Azure Functions (.NET Isolated Worker)
- **Target Ecosystem:** Microsoft Entra ID (Free tier compatible, zero P1/P2 dependency)

---

## 1. Executive Summary & Context

Modern organizations require automated Identity Governance and Administration (IGA) / Identity Lifecycle Management (ILM) to synchronize Human Capital Management (HCM) employee lifecycles with digital cloud identities in Microsoft Entra ID.

In standard enterprise environments, automated provisioning and dynamic security group memberships often rely on premium licenses (Entra ID P1/P2 or Microsoft Entra Governance). For organizations on **Microsoft Entra ID Free**, this solution acts as a dedicated, self-hosted, enterprise-grade **IAM Lifecycle Engine**.

This engine runs as an automated serverless job (Azure Functions) and provides a local Command Line Interface (CLI) for administration, local testing, and dry-run audits. It adheres to **Clean Architecture / Ports & Adapters (Hexagonal Architecture)** and **Domain-Driven Design (DDD)**, ensuring testability, zero vendor lock-in, and an open-source connector ecosystem.

---

## 2. Core Architectural Principles

1. **HCM as Authoritative Source of Truth:**
   The HCM system determines employee existence, legal name, employment status (Active/Inactive), organizational attributes (Department, Role, Cost Center), and organizational events. The Entra ID directory does not dictate functional status.

2. **Entra ID as Identity Provider (IdP):**
   Entra ID hosts the digital identity, authentication mechanisms, and access groups.

3. **Desired State vs. Actual State (Continuous Reconciliation):**
   The architecture strictly rejects imperative procedural scripts (`read employee -> call Graph`). Instead, it implements a declarative reconciler:
   $$\text{HCM Data} \xrightarrow{\text{Rules Engine}} \text{Desired State} \iff \text{Entra ID Actual State} \implies \text{Idempotent Delta ChangeSet}$$

4. **Idempotency:**
   Executing the synchronization cycle multiple times against unchanged HCM data produces zero mutations against Microsoft Graph (`ChangeSet.IsEmpty == true`).

5. **Principle of Least Privilege (PoLP) & Zero Trust:**
   The application uses scoped Microsoft Graph permissions (`User.ReadWrite.All`, `GroupMember.ReadWrite.All`) via Azure Managed Identity. Secrets and temporary credentials are never stored, logged, or serialized.

6. **Data Minimization (GDPR / LGPD Compliance):**
   Only data strictly required for identity lifecycle management is processed into Entra ID (`employeeId`, `displayName`, `userPrincipalName`, `accountEnabled`, group memberships). Personal PII (national IDs, compensation, home addresses) is discarded at the ingress layer.

7. **Extensibility & Open Source Readiness (SPI Pattern):**
   Connectors for HCM systems and notification transports are decoupled behind clean domain interfaces (Ports), allowing community contributions (e.g., Workday, TOTVS, Senior, GraphQL, SMS) without altering the core engine.

---

## 3. System Architecture & Project Structure

The solution follows Clean Architecture with a strict inward dependency rule:

```text
HcmIdentityProvisioning.sln
│
├── src/
│   ├── HcmIdentityProvisioning.Domain/          # Pure Domain Core (Zero external packages)
│   │   ├── Common/                              # Result<T, E>, ValueObject base, Entity base
│   │   ├── Entities/                            # Employee, EntraUser, ManagedGroup
│   │   ├── Enums/                               # EmployeeStatus, DeltaActionType
│   │   ├── ValueObjects/                        # EmployeeId, UserPrincipalName, EmailAddress
│   │   ├── Actions/                             # DeltaAction hierarchy (Create, Update, Enable, Disable, etc.)
│   │   ├── Ports/                               # IHcmConnector, IIdentityStore, IRulesEngine, ICredentialDeliveryService
│   │   └── Policies/                            # ICircuitBreaker, ISecurePasswordGenerator
│   │
│   ├── HcmIdentityProvisioning.Application/     # Use Cases & Orchestration
│   │   ├── UseCases/                            # ReconcileBatchUseCase, DryRunAuditUseCase
│   │   ├── Models/                              # SyncBatchContext, ChangeSet, DesiredState, SyncReport
│   │   ├── Options/                             # SyncSettings, CircuitBreakerSettings
│   │   └── Services/                            # IdentityReconciliationService
│   │
│   ├── HcmIdentityProvisioning.Infrastructure/  # Concrete Adapters & I/O
│   │   ├── Connectors/
│   │   │   ├── Synthetic/                       # SyntheticHcmConnector + synthetic-employees.json
│   │   │   └── Rest/                            # GenericRestHcmConnector (IHttpClientFactory + Resilience)
│   │   ├── Graph/
│   │   │   ├── EntraIdGraphAdapter.cs           # Microsoft.Graph SDK v5+ & Azure.Identity
│   │   │   └── InMemoryIdentityStore.cs         # Thread-safe in-memory store for offline tests and CLI
│   │   ├── Rules/
│   │   │   └── MicrosoftRulesEngineAdapter.cs   # Microsoft.RulesEngine wrapper loading rules.json
│   │   ├── Notifications/
│   │   │   ├── EmailCredentialDeliveryService.cs
│   │   │   └── MockCredentialDeliveryService.cs
│   │   └── Security/
│   │       ├── SecurePasswordGenerator.cs       # Cryptographic CSPRNG generator
│   │       └── UpnSanitizer.cs                  # Diacritic stripping and collision resolver
│   │
│   ├── HcmIdentityProvisioning.Cli/             # Console Runner
│   │   ├── Commands/                            # SyncCommand, ValidateRulesCommand
│   │   └── Program.cs                           # CLI entrypoint with System.CommandLine
│   │
│   └── HcmIdentityProvisioning.Functions/       # Serverless Azure Functions (.NET 10 Isolated)
│       ├── Program.cs                           # Host setup, DI, native telemetry
│       ├── Functions/
│       │   ├── SyncTimerFunction.cs             # Scheduled cron trigger
│       │   └── SyncHttpFunction.cs              # Authenticated manual/webhook trigger (?dry_run=true)
│       └── host.json
│
└── tests/
    ├── HcmIdentityProvisioning.Domain.Tests/
    ├── HcmIdentityProvisioning.Application.Tests/
    └── HcmIdentityProvisioning.Infrastructure.Tests/
```

---

## 4. Domain Model & Invariants

### 4.1 Value Objects & Immutability

* **`EmployeeId`**:
  Immutable business identifier from HCM. Validated at construction; cannot be null, empty, or whitespace.
* **`UserPrincipalName`**:
  Valid RFC/Entra ID format: `{slug}@{tenantDomain}`.
  - Sanitization logic strips diacritics (e.g., `ã`, `é`, `ç` $\to$ `a`, `e`, `c`), converts to lowercase, and strips unauthorized symbols.
  - Homonym collision strategy: if `first.last@domain.com` is already bound to a different `employeeId`, appends a deterministic incremental counter (`first.last2@domain.com`).
* **`Employee` Entity**:
  ```csharp
  public sealed record Employee(
      EmployeeId Id,
      string FullName,
      EmployeeStatus Status,
      string Department,
      string JobTitle,
      IReadOnlyDictionary<string, string> ExtendedAttributes
  );
  ```
  `ExtendedAttributes` holds dynamic communication/metadata values (e.g., `email`, `phone`) without coupling the domain entity to delivery transports.

* **`EntraUser` Entity**:
  ```csharp
  public sealed record EntraUser(
      Guid GraphId,
      EmployeeId EmployeeId,
      UserPrincipalName UserPrincipalName,
      string DisplayName,
      bool AccountEnabled,
      IReadOnlySet<Guid> AssignedGroupIds
  );
  ```

### 4.2 Delta Action Hierarchy

The reconciliation engine calculates an explicit list of domain actions:
* `CreateUserAction(Employee Employee, UserPrincipalName Upn, string TempPassword)`
* `UpdateDisplayNameAction(Guid GraphId, string NewDisplayName)`
* `EnableAccountAction(Guid GraphId)`
* `DisableAccountAction(Guid GraphId)`
* `RevokeSessionsAction(Guid GraphId)`
* `AddGroupMemberAction(Guid GraphId, Guid GroupId, string GroupName)`
* `RemoveGroupMemberAction(Guid GraphId, Guid GroupId, string GroupName)`

---

## 5. Group Governance & Rules Engine

### 5.1 Technology: `Microsoft.RulesEngine`

Rules are stored in an external declarative JSON document (`rules.json`), evaluated in-process via `Microsoft.RulesEngine`:

```json
[
  {
    "WorkflowName": "EntraIdGroupAssignment",
    "Rules": [
      {
        "RuleName": "FinanceDepartmentAccess",
        "SuccessEvent": "grp-iam-finance",
        "Expression": "Department == \"Financeiro\""
      },
      {
        "RuleName": "EngineeringAccess",
        "SuccessEvent": "grp-iam-engineering",
        "Expression": "Department == \"Tecnologia\" AND JobTitle.Contains(\"Engenheiro\")"
      }
    ]
  }
]
```

### 5.2 Managed Group Boundary (Zero Blast Radius)

To protect administrative, external, and ad-hoc groups created in the Entra tenant:
1. The engine defines a **Managed Group Boundary** (`ManagedGroupScope`), identified by:
   - Configured prefix (e.g., `grp-iam-*`); and/or
   - Explicit registration in `rules.json`.
2. The reconciler **never removes** a user from any group outside this scope. Manual assignments to unmanaged groups (e.g., `SG-AzureAdmins`, `All-Company`) remain untouched.

---

## 6. Security Defenses & Resiliency

### 6.1 Massive Disablement Circuit Breaker

To defend against corrupt data, empty payloads, or upstream API outages flagging active employees as deleted:
* **`ICircuitBreaker`**:
  - Computes disablement volume:
    $$\text{DisablementRate} = \frac{\text{Count}(\text{DisableActions})}{\text{BatchSize}}$$
  - If $\text{DisablementRate} > \text{MaxDisablementPercentage}$ (default: `10%`) OR $\text{Count} > \text{MaxDisablementCount}$ (default: `25`):
    - The circuit breaker **trips**.
    - All destructive actions in the batch are halted.
    - An immediate critical alert (`CRITICAL_AUDIT_BREAKER_TRIPPED`) is logged.
    - Non-destructive operations (creating new joiners) can either be preserved or halted based on configuration.

### 6.2 Temporary Password Generation & Delivery

* **`ISecurePasswordGenerator`**: Uses `System.Security.Cryptography.RandomNumberGenerator` to generate a 24-character cryptographic password satisfying Entra ID complexity requirements.
* Flag `forceChangePasswordNextSignIn: true` is always enforced.
* **Delivery Channel (`ICredentialDeliveryService`)**:
  - `EmailCredentialDeliveryService`: Checks `employee.ExtendedAttributes["email"]`. If present, dispatches the initial onboarding message out-of-band.
  - If missing or empty: Logs a warning (`CREDENTIAL_DELIVERY_SKIPPED`), completes user provisioning, and leaves password distribution to the standard IT Service Desk procedure on Day 1.

### 6.3 Agnostic Observability & Structured Logging

* **Core Layers:** Depend solely on `Microsoft.Extensions.Logging.Abstractions` with semantic message templates (`_logger.LogInformation("Action {Action} for {EmployeeId}", action, employeeId)`).
* **Azure Functions:** Automatically piped to Azure Application Insights and Log Analytics (`customDimensions`).
* **CLI:**
  - Default: Human-readable console with color-coded operational summaries.
  - `--json-logs`: Single-line structured JSON (NDJSON) suitable for pipe consumption by SIEMs (Splunk, Elastic, Sentinel).

---

## 7. Lifecycle Event Workflows

### 7.1 Joiner Workflow (Employee Onboarding)
```text
HCM: Active
Entra ID: Missing

1. Check UPN availability; resolve homonym collision if necessary.
2. Generate secure temporary password.
3. POST /users (employeeId, displayName, UPN, passwordProfile with forceChange=true).
4. Evaluate rules.json -> compute desired groups.
5. Add user to desired managed groups.
6. Dispatch initial credentials to employee.ExtendedAttributes["email"] via ICredentialDeliveryService.
```

### 7.2 Mover Workflow (Role / Department Change)
```text
HCM: Active
Entra ID: Exists, Enabled

1. Compare current displayName with HCM FullName -> update if changed.
2. Evaluate rules.json based on new Department/Role -> compute DesiredGroups.
3. Compare DesiredGroups against ActualGroups (restricted to ManagedGroupScope).
4. Delta Add: Add memberships for groups in Desired and not in Actual.
5. Delta Remove: Remove memberships for groups in Actual, within ManagedGroupScope, and not in Desired.
```

### 7.3 Leaver Workflow (Offboarding / Termination)
```text
HCM: Inactive / Terminated
Entra ID: Exists, Enabled

1. PATCH /users/{id} -> accountEnabled = false.
2. POST /users/{id}/revokeSignInSessions -> immediately invalidates active browser/refresh tokens.
3. Remove user from all groups within ManagedGroupScope.
4. Account is retained in disabled state for audit/retention policies.
```

---

## 8. Pagination, Batching & Memory Efficiency

The engine processes HCM records in pages (`PAGE_SIZE = 50`):
1. Ingest Page $N$ from `IHcmConnector`.
2. Compute Desired State for all employees in Page $N$.
3. Query `IIdentityStore` once for the entire batch:
   `GET /users?$filter=employeeId in ('101', '102', ...)`
4. Compute Delta ChangeSet in memory.
5. Evaluate Circuit Breaker.
6. Dispatch mutations via Microsoft Graph Batching (`BatchRequestContentCollection`, up to 20 sub-requests per payload).
7. Commit audit log, drop Page $N$ from memory, invoke GC friendly cleanup.
8. Repeat for Page $N+1$.

---

## 9. Connector Ecosystem & Synthetic Fixtures

### 9.1 Open-Source Contributor Contract (`IHcmConnector`)

Any third-party HCM can be supported by implementing:
```csharp
public interface IHcmConnector
{
    Task<PagedResult<Employee>> GetEmployeesPageAsync(
        int pageNumber,
        int pageSize,
        CancellationToken ct = default);
}
```

A dedicated guide (`docs/connectors-guide.md`) documents how to author and register new adapters with `IServiceCollection`.

### 9.2 Synthetic Fixture Catalog (`fixtures/synthetic-employees.json`)

Included for offline demonstration and deterministic testing:
* **Scenario 1 (Joiner):** New active employee $\to$ created, groups assigned.
* **Scenario 2 (Mover):** Department changed from Finance to Tech $\to$ group adjusted.
* **Scenario 3 (Leaver):** Inactive employee $\to$ account disabled, sessions revoked, groups purged.
* **Scenario 4 (Homonym Collision):** Second employee with matching name $\to$ UPN assigned with counter suffix.
* **Scenario 5 (Diacritics):** Employee with accents (`José d'Ávila`) $\to$ sanitized UPN (`jose.davila@...`).
* **Scenario 6 (Idempotent No-Op):** Employee in sync $\to$ zero actions.

---

## 10. Testing Strategy

1. **Unit Tests (`Domain.Tests`):**
   - Value object invariants (`EmployeeId`, `UserPrincipalName`).
   - UPN diacritic stripping and collision numbering.
   - Cryptographic entropy of `SecurePasswordGenerator`.
   - Circuit breaker threshold triggers.
   - `Microsoft.RulesEngine` policy evaluations.
2. **Application Tests (`Application.Tests`):**
   - `ReconcileBatchUseCase` test matrix using `NSubstitute` for ports.
   - Joiner, Mover, Leaver, Idempotency, and Dry-Run scenarios.
3. **Integration & E2E Tests (`Infrastructure.Tests`):**
   - `SyntheticHcmConnector` against `InMemoryIdentityStore`.
   - Verification of full batch cycle execution in < 1 second.
   - Zero-allocation memory stability checks.

---

## 11. Security & Compliance Assessment

| Threat / Risk | Architectural Mitigation |
| :--- | :--- |
| **Accidental Mass Deletion/Disablement** | Disablement Circuit Breaker halts batch if threshold is exceeded. |
| **Credential Leakage in Logs** | Ephemeral passwords; CSPRNG generation; explicit exclusion from logging pipelines. |
| **Excessive Privilege (Graph API)** | Minimal App Roles (`User.ReadWrite.All`, `GroupMember.ReadWrite.All`); no tenant-wide Directory.ReadWrite.All or Global Admin credentials. |
| **Cloud Credential Storage in Code** | Azure Managed Identity in production (`DefaultAzureCredential`); no static secrets committed. |
| **API Throttling / HTTP 429** | Microsoft Graph batching (20/batch) and automatic exponential backoff honoring `Retry-After`. |
| **Administrative Group Tampering** | Strict Managed Group Boundary (`grp-iam-*`); zero touches to unmanaged groups. |
