# HCM → Microsoft Entra ID Provisioning: Ciclo 1 (Núcleo do Motor & CLI Sandbox) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implementar o núcleo desacoplado do motor de governança de identidades e reconciliação declarativa, com suporte completo aos 6 cenários de RH (Joiner, Mover, Leaver, Homônimos, Diacríticos e Idempotência) e um utilitário CLI executável localmente e 100% autossuficiente (sem necessidade de conexão ou credenciais de nuvem).

**Architecture:** Clean Architecture / Hexagonal Architecture em .NET 10. Camada de Domínio pura (C# 14, zero dependências externas), motor declarativo de reconciliação em Aplicação (`DesiredState` vs `ActualState` gerando `ChangeSet` idempotente), adaptadores de Infraestrutura em memória (RulesEngine, gerador CSPRNG, Conector Sintético com fixtures e In-Memory Store thread-safe) e CLI em `System.CommandLine`.

**Tech Stack:** .NET 10 (`net10.0`), C# 14, `RulesEngine` (5.0.3), `System.CommandLine` (2.0.0-beta4), xUnit, `FluentAssertions`, `NSubstitute`.

**Spec:** [`docs/superpowers/specs/2026-10-05-hcm-entra-id-provisioning-design.md`](file:///d:/Projects/hcm-identity-provisioning-automation/docs/superpowers/specs/2026-10-05-hcm-entra-id-provisioning-design.md)

## Global Constraints

- Target Framework: `net10.0` com C# 14 (`<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`).
- Pure Domain: `HcmIdentityProvisioning.Domain` não possui nenhuma referência a pacotes NuGet externos.
- Managed Group Boundary: O motor só altera associações de grupos que comecem com `grp-iam-` ou declarados em `rules.json`. Grupos fora desse escopo permanecem intocados.
- Missing Groups Policy: Se um grupo gerenciado indicado pelas regras não existir na store, emite aviso estruturado `WARNING_MANAGED_GROUP_NOT_FOUND` e ignora a atribuição daquele grupo sem interromper a sincronização do colaborador ou do lote.
- Circuit Breaker: Desarma se a taxa de desativação do lote for > 10% ou contagem > 25, suspendendo ações destrutivas (`DisableAccountAction`, `RevokeSessionsAction`).
- Credential Safety: Senhas temporárias são geradas via CSPRNG de 24 caracteres e nunca são expostas em logs.

---

### Task 1: Scaffolding da Solução e Projetos do Ciclo 1

**Files:**
- Create: `HcmIdentityProvisioning.sln`
- Create: `src/HcmIdentityProvisioning.Domain/HcmIdentityProvisioning.Domain.csproj`
- Create: `src/HcmIdentityProvisioning.Application/HcmIdentityProvisioning.Application.csproj`
- Create: `src/HcmIdentityProvisioning.Infrastructure/HcmIdentityProvisioning.Infrastructure.csproj`
- Create: `src/HcmIdentityProvisioning.Cli/HcmIdentityProvisioning.Cli.csproj`
- Create: `tests/HcmIdentityProvisioning.Domain.Tests/HcmIdentityProvisioning.Domain.Tests.csproj`
- Create: `tests/HcmIdentityProvisioning.Application.Tests/HcmIdentityProvisioning.Application.Tests.csproj`
- Create: `tests/HcmIdentityProvisioning.Infrastructure.Tests/HcmIdentityProvisioning.Infrastructure.Tests.csproj`

**Interfaces:**
- Consumes: None
- Produces: Estrutura completa de solução .NET 10 compilável com referências arquiteturais estritas.

- [ ] **Step 1: Criar solução e projetos via .NET CLI**

```powershell
dotnet new sln -n HcmIdentityProvisioning
dotnet new classlib -n HcmIdentityProvisioning.Domain -o src/HcmIdentityProvisioning.Domain -f net10.0
dotnet new classlib -n HcmIdentityProvisioning.Application -o src/HcmIdentityProvisioning.Application -f net10.0
dotnet new classlib -n HcmIdentityProvisioning.Infrastructure -o src/HcmIdentityProvisioning.Infrastructure -f net10.0
dotnet new console -n HcmIdentityProvisioning.Cli -o src/HcmIdentityProvisioning.Cli -f net10.0
dotnet new xunit -n HcmIdentityProvisioning.Domain.Tests -o tests/HcmIdentityProvisioning.Domain.Tests -f net10.0
dotnet new xunit -n HcmIdentityProvisioning.Application.Tests -o tests/HcmIdentityProvisioning.Application.Tests -f net10.0
dotnet new xunit -n HcmIdentityProvisioning.Infrastructure.Tests -o tests/HcmIdentityProvisioning.Infrastructure.Tests -f net10.0
```

- [ ] **Step 2: Configurar referências entre projetos e adicionar à solução**

```powershell
dotnet sln add (Get-ChildItem -Recurse -Filter *.csproj | ForEach-Object { $_.FullName })

# Inward dependency rule
dotnet add src/HcmIdentityProvisioning.Application/HcmIdentityProvisioning.Application.csproj reference src/HcmIdentityProvisioning.Domain/HcmIdentityProvisioning.Domain.csproj
dotnet add src/HcmIdentityProvisioning.Infrastructure/HcmIdentityProvisioning.Infrastructure.csproj reference src/HcmIdentityProvisioning.Domain/HcmIdentityProvisioning.Domain.csproj
dotnet add src/HcmIdentityProvisioning.Infrastructure/HcmIdentityProvisioning.Infrastructure.csproj reference src/HcmIdentityProvisioning.Application/HcmIdentityProvisioning.Application.csproj
dotnet add src/HcmIdentityProvisioning.Cli/HcmIdentityProvisioning.Cli.csproj reference src/HcmIdentityProvisioning.Infrastructure/HcmIdentityProvisioning.Infrastructure.csproj

# Test references
dotnet add tests/HcmIdentityProvisioning.Domain.Tests/HcmIdentityProvisioning.Domain.Tests.csproj reference src/HcmIdentityProvisioning.Domain/HcmIdentityProvisioning.Domain.csproj
dotnet add tests/HcmIdentityProvisioning.Application.Tests/HcmIdentityProvisioning.Application.Tests.csproj reference src/HcmIdentityProvisioning.Application/HcmIdentityProvisioning.Application.csproj
dotnet add tests/HcmIdentityProvisioning.Infrastructure.Tests/HcmIdentityProvisioning.Infrastructure.Tests.csproj reference src/HcmIdentityProvisioning.Infrastructure/HcmIdentityProvisioning.Infrastructure.csproj
```

- [ ] **Step 3: Instalar pacotes de teste e limpar arquivos de template**

```powershell
Get-ChildItem -Recurse -Filter "Class1.cs" | Remove-Item -Force
Get-ChildItem -Recurse -Filter "UnitTest1.cs" | Remove-Item -Force

dotnet add tests/HcmIdentityProvisioning.Domain.Tests package FluentAssertions
dotnet add tests/HcmIdentityProvisioning.Application.Tests package FluentAssertions
dotnet add tests/HcmIdentityProvisioning.Application.Tests package NSubstitute
dotnet add tests/HcmIdentityProvisioning.Infrastructure.Tests package FluentAssertions
dotnet add tests/HcmIdentityProvisioning.Infrastructure.Tests package NSubstitute
```

- [ ] **Step 4: Compilar e verificar solução**

Run: `dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 5: Commit**

```bash
git add HcmIdentityProvisioning.sln src/ tests/
git commit -m "chore: scaffold .NET 10 solution and projects for Cycle 1"
```

---

### Task 2: Domain Common Primitives & Value Objects

**Files:**
- Create: `src/HcmIdentityProvisioning.Domain/Common/Result.cs`
- Create: `src/HcmIdentityProvisioning.Domain/ValueObjects/EmployeeId.cs`
- Create: `src/HcmIdentityProvisioning.Domain/ValueObjects/UserPrincipalName.cs`
- Create: `src/HcmIdentityProvisioning.Domain/ValueObjects/EmailAddress.cs`
- Test: `tests/HcmIdentityProvisioning.Domain.Tests/ValueObjects/EmployeeIdTests.cs`
- Test: `tests/HcmIdentityProvisioning.Domain.Tests/ValueObjects/UserPrincipalNameTests.cs`

**Interfaces:**
- Consumes: None
- Produces:
  - `Result<TValue, TError>.Success(TValue)` / `Failure(TError)`
  - `EmployeeId.Create(string? value)`
  - `UserPrincipalName.Create(string? value)`
  - `EmailAddress.Create(string? value)`

- [ ] **Step 1: Escrever testes que falham para EmployeeId e UserPrincipalName**

```csharp
// tests/HcmIdentityProvisioning.Domain.Tests/ValueObjects/EmployeeIdTests.cs
using FluentAssertions;
using HcmIdentityProvisioning.Domain.ValueObjects;
using Xunit;

namespace HcmIdentityProvisioning.Domain.Tests.ValueObjects;

public class EmployeeIdTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithInvalidValue_ShouldFail(string? raw)
    {
        var result = EmployeeId.Create(raw);
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Create_WithValidValue_ShouldSucceed()
    {
        var result = EmployeeId.Create("EMP-00123");
        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be("EMP-00123");
    }
}
```

```csharp
// tests/HcmIdentityProvisioning.Domain.Tests/ValueObjects/UserPrincipalNameTests.cs
using FluentAssertions;
using HcmIdentityProvisioning.Domain.ValueObjects;
using Xunit;

namespace HcmIdentityProvisioning.Domain.Tests.ValueObjects;

public class UserPrincipalNameTests
{
    [Theory]
    [InlineData("invalid-upn")]
    [InlineData("@company.com")]
    [InlineData("user@")]
    public void Create_WithInvalidFormat_ShouldFail(string raw)
    {
        var result = UserPrincipalName.Create(raw);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Create_WithValidUpn_ShouldNormalizeToLowercase()
    {
        var result = UserPrincipalName.Create("John.Doe@Company.COM");
        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be("john.doe@company.com");
        result.Value.Username.Should().Be("john.doe");
        result.Value.Domain.Should().Be("company.com");
    }
}
```

- [ ] **Step 2: Executar testes para verificar falha de compilação**

Run: `dotnet test tests/HcmIdentityProvisioning.Domain.Tests --filter FullyQualifiedName~ValueObjects`
Expected: FAIL (tipos não existem).

- [ ] **Step 3: Implementar Result<TValue, TError> e os Value Objects**

```csharp
// src/HcmIdentityProvisioning.Domain/Common/Result.cs
namespace HcmIdentityProvisioning.Domain.Common;

public readonly struct Result<TValue, TError>
{
    public bool IsSuccess { get; }
    public TValue Value { get; }
    public TError Error { get; }

    private Result(TValue value)
    {
        IsSuccess = true;
        Value = value;
        Error = default!;
    }

    private Result(TError error)
    {
        IsSuccess = false;
        Value = default!;
        Error = error;
    }

    public static Result<TValue, TError> Success(TValue value) => new(value);
    public static Result<TValue, TError> Failure(TError error) => new(error);
}
```

```csharp
// src/HcmIdentityProvisioning.Domain/ValueObjects/EmployeeId.cs
using HcmIdentityProvisioning.Domain.Common;

namespace HcmIdentityProvisioning.Domain.ValueObjects;

public sealed record EmployeeId
{
    public string Value { get; }

    private EmployeeId(string value) => Value = value;

    public static Result<EmployeeId, string> Create(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Result<EmployeeId, string>.Failure("EmployeeId cannot be null or whitespace.");

        return Result<EmployeeId, string>.Success(new EmployeeId(value.Trim()));
    }

    public override string ToString() => Value;
}
```

```csharp
// src/HcmIdentityProvisioning.Domain/ValueObjects/UserPrincipalName.cs
using HcmIdentityProvisioning.Domain.Common;

namespace HcmIdentityProvisioning.Domain.ValueObjects;

public sealed record UserPrincipalName
{
    public string Value { get; }
    public string Username { get; }
    public string Domain { get; }

    private UserPrincipalName(string value, string username, string domain)
    {
        Value = value;
        Username = username;
        Domain = domain;
    }

    public static Result<UserPrincipalName, string> Create(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Result<UserPrincipalName, string>.Failure("UPN cannot be null or whitespace.");

        var trimmed = value.Trim().ToLowerInvariant();
        var parts = trimmed.Split('@');
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]) || !parts[1].Contains('.'))
            return Result<UserPrincipalName, string>.Failure($"Invalid UPN format: '{value}'.");

        return Result<UserPrincipalName, string>.Success(new UserPrincipalName(trimmed, parts[0], parts[1]));
    }

    public override string ToString() => Value;
}
```

```csharp
// src/HcmIdentityProvisioning.Domain/ValueObjects/EmailAddress.cs
using HcmIdentityProvisioning.Domain.Common;

namespace HcmIdentityProvisioning.Domain.ValueObjects;

public sealed record EmailAddress
{
    public string Value { get; }

    private EmailAddress(string value) => Value = value;

    public static Result<EmailAddress, string> Create(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.Contains('@') || !value.Contains('.'))
            return Result<EmailAddress, string>.Failure("Invalid email address format.");

        return Result<EmailAddress, string>.Success(new EmailAddress(value.Trim().ToLowerInvariant()));
    }

    public override string ToString() => Value;
}
```

- [ ] **Step 4: Executar testes para verificar aprovação**

Run: `dotnet test tests/HcmIdentityProvisioning.Domain.Tests --filter FullyQualifiedName~ValueObjects`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/HcmIdentityProvisioning.Domain/ tests/HcmIdentityProvisioning.Domain.Tests/
git commit -m "feat(domain): add Result primitive and Value Objects (EmployeeId, UserPrincipalName, EmailAddress)"
```

---

### Task 3: Domain Entities, Enums & DeltaAction Hierarchy

**Files:**
- Create: `src/HcmIdentityProvisioning.Domain/Enums/EmployeeStatus.cs`
- Create: `src/HcmIdentityProvisioning.Domain/Entities/Employee.cs`
- Create: `src/HcmIdentityProvisioning.Domain/Entities/EntraUser.cs`
- Create: `src/HcmIdentityProvisioning.Domain/Entities/ManagedGroup.cs`
- Create: `src/HcmIdentityProvisioning.Domain/Actions/DeltaAction.cs`
- Test: `tests/HcmIdentityProvisioning.Domain.Tests/Entities/EntityTests.cs`

**Interfaces:**
- Consumes: `EmployeeId`, `UserPrincipalName`
- Produces:
  - `EmployeeStatus.Active`, `EmployeeStatus.Inactive`
  - `Employee`, `EntraUser`, `ManagedGroup`
  - `DeltaAction` (subtipos: `CreateUserAction`, `UpdateDisplayNameAction`, `EnableAccountAction`, `DisableAccountAction`, `RevokeSessionsAction`, `AddGroupMemberAction`, `RemoveGroupMemberAction`)

- [ ] **Step 1: Escrever teste para entidades e polimorfismo de ações**

```csharp
// tests/HcmIdentityProvisioning.Domain.Tests/Entities/EntityTests.cs
using FluentAssertions;
using HcmIdentityProvisioning.Domain.Actions;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Enums;
using HcmIdentityProvisioning.Domain.ValueObjects;
using Xunit;

namespace HcmIdentityProvisioning.Domain.Tests.Entities;

public class EntityTests
{
    [Fact]
    public void Employee_ShouldHoldAttributesCorrectly()
    {
        var empId = EmployeeId.Create("EMP101").Value;
        var emp = new Employee(
            empId,
            "Carlos Silva",
            EmployeeStatus.Active,
            "Tecnologia",
            "Engenheiro de Software",
            new Dictionary<string, string> { ["email"] = "carlos@personal.com" }
        );

        emp.FullName.Should().Be("Carlos Silva");
        emp.Status.Should().Be(EmployeeStatus.Active);
        emp.ExtendedAttributes["email"].Should().Be("carlos@personal.com");
    }

    [Fact]
    public void DeltaActionHierarchy_ShouldBePolymorphic()
    {
        var userId = Guid.NewGuid();
        var actions = new DeltaAction[]
        {
            new EnableAccountAction(userId),
            new RevokeSessionsAction(userId)
        };

        actions.Should().HaveCount(2);
        actions[0].TargetGraphId.Should().Be(userId);
    }
}
```

- [ ] **Step 2: Executar teste para verificar falha**

Run: `dotnet test tests/HcmIdentityProvisioning.Domain.Tests --filter FullyQualifiedName~EntityTests`
Expected: FAIL.

- [ ] **Step 3: Implementar Enums, Entidades e DeltaActions**

```csharp
// src/HcmIdentityProvisioning.Domain/Enums/EmployeeStatus.cs
namespace HcmIdentityProvisioning.Domain.Enums;

public enum EmployeeStatus
{
    Active,
    Inactive
}
```

```csharp
// src/HcmIdentityProvisioning.Domain/Entities/Employee.cs
using HcmIdentityProvisioning.Domain.Enums;
using HcmIdentityProvisioning.Domain.ValueObjects;

namespace HcmIdentityProvisioning.Domain.Entities;

public sealed record Employee(
    EmployeeId Id,
    string FullName,
    EmployeeStatus Status,
    string Department,
    string JobTitle,
    IReadOnlyDictionary<string, string> ExtendedAttributes
);
```

```csharp
// src/HcmIdentityProvisioning.Domain/Entities/EntraUser.cs
using HcmIdentityProvisioning.Domain.ValueObjects;

namespace HcmIdentityProvisioning.Domain.Entities;

public sealed record EntraUser(
    Guid GraphId,
    EmployeeId EmployeeId,
    UserPrincipalName UserPrincipalName,
    string DisplayName,
    bool AccountEnabled,
    IReadOnlySet<Guid> AssignedGroupIds
);
```

```csharp
// src/HcmIdentityProvisioning.Domain/Entities/ManagedGroup.cs
namespace HcmIdentityProvisioning.Domain.Entities;

public sealed record ManagedGroup(
    Guid Id,
    string DisplayName
);
```

```csharp
// src/HcmIdentityProvisioning.Domain/Actions/DeltaAction.cs
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.ValueObjects;

namespace HcmIdentityProvisioning.Domain.Actions;

public abstract record DeltaAction(Guid? TargetGraphId)
{
    public abstract string ActionName { get; }
}

public sealed record CreateUserAction(
    Employee Employee,
    UserPrincipalName UserPrincipalName,
    string TemporaryPassword
) : DeltaAction(null)
{
    public override string ActionName => "CreateUser";
}

public sealed record UpdateDisplayNameAction(
    Guid GraphId,
    string NewDisplayName
) : DeltaAction(GraphId)
{
    public override string ActionName => "UpdateDisplayName";
}

public sealed record EnableAccountAction(
    Guid GraphId
) : DeltaAction(GraphId)
{
    public override string ActionName => "EnableAccount";
}

public sealed record DisableAccountAction(
    Guid GraphId
) : DeltaAction(GraphId)
{
    public override string ActionName => "DisableAccount";
}

public sealed record RevokeSessionsAction(
    Guid GraphId
) : DeltaAction(GraphId)
{
    public override string ActionName => "RevokeSessions";
}

public sealed record AddGroupMemberAction(
    Guid GraphId,
    Guid GroupId,
    string GroupName
) : DeltaAction(GraphId)
{
    public override string ActionName => "AddGroupMember";
}

public sealed record RemoveGroupMemberAction(
    Guid GraphId,
    Guid GroupId,
    string GroupName
) : DeltaAction(GraphId)
{
    public override string ActionName => "RemoveGroupMember";
}
```

- [ ] **Step 4: Executar testes para verificar aprovação**

Run: `dotnet test tests/HcmIdentityProvisioning.Domain.Tests --filter FullyQualifiedName~EntityTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/HcmIdentityProvisioning.Domain/ tests/HcmIdentityProvisioning.Domain.Tests/
git commit -m "feat(domain): add Employee, EntraUser, ManagedGroup entities and DeltaAction hierarchy"
```

---

### Task 4: Domain Ports & Service Interfaces

**Files:**
- Create: `src/HcmIdentityProvisioning.Domain/Common/PagedResult.cs`
- Create: `src/HcmIdentityProvisioning.Domain/Ports/IHcmConnector.cs`
- Create: `src/HcmIdentityProvisioning.Domain/Ports/IIdentityStore.cs`
- Create: `src/HcmIdentityProvisioning.Domain/Ports/IRulesEngine.cs`
- Create: `src/HcmIdentityProvisioning.Domain/Ports/ICredentialDeliveryService.cs`
- Create: `src/HcmIdentityProvisioning.Domain/Policies/ICircuitBreaker.cs`
- Create: `src/HcmIdentityProvisioning.Domain/Policies/ISecurePasswordGenerator.cs`

**Interfaces:**
- Consumes: Entidades, ValueObjects, Ações
- Produces: Contratos de portas e políticas hexagonais.

- [ ] **Step 1: Implementar PagedResult<T>**

```csharp
// src/HcmIdentityProvisioning.Domain/Common/PagedResult.cs
namespace HcmIdentityProvisioning.Domain.Common;

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int PageNumber,
    int PageSize,
    int TotalCount,
    bool HasNextPage
);
```

- [ ] **Step 2: Implementar Portas e Políticas do Domínio**

```csharp
// src/HcmIdentityProvisioning.Domain/Ports/IHcmConnector.cs
using HcmIdentityProvisioning.Domain.Common;
using HcmIdentityProvisioning.Domain.Entities;

namespace HcmIdentityProvisioning.Domain.Ports;

public interface IHcmConnector
{
    Task<PagedResult<Employee>> GetEmployeesPageAsync(
        int pageNumber,
        int pageSize,
        CancellationToken ct = default);
}
```

```csharp
// src/HcmIdentityProvisioning.Domain/Ports/IIdentityStore.cs
using HcmIdentityProvisioning.Domain.Actions;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.ValueObjects;

namespace HcmIdentityProvisioning.Domain.Ports;

public interface IIdentityStore
{
    Task<IReadOnlyDictionary<EmployeeId, EntraUser>> GetUsersByEmployeeIdsAsync(
        IEnumerable<EmployeeId> employeeIds,
        CancellationToken ct = default);

    Task<bool> IsUserPrincipalNameAvailableAsync(
        UserPrincipalName upn,
        CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, ManagedGroup>> GetManagedGroupsAsync(
        CancellationToken ct = default);

    Task ApplyBatchMutationsAsync(
        IEnumerable<DeltaAction> actions,
        CancellationToken ct = default);
}
```

```csharp
// src/HcmIdentityProvisioning.Domain/Ports/IRulesEngine.cs
using HcmIdentityProvisioning.Domain.Entities;

namespace HcmIdentityProvisioning.Domain.Ports;

public interface IRulesEngine
{
    Task<IReadOnlySet<string>> EvaluateDesiredGroupsAsync(
        Employee employee,
        CancellationToken ct = default);
}
```

```csharp
// src/HcmIdentityProvisioning.Domain/Ports/ICredentialDeliveryService.cs
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.ValueObjects;

namespace HcmIdentityProvisioning.Domain.Ports;

public interface ICredentialDeliveryService
{
    Task DeliverInitialCredentialsAsync(
        Employee employee,
        UserPrincipalName upn,
        string temporaryPassword,
        CancellationToken ct = default);
}
```

```csharp
// src/HcmIdentityProvisioning.Domain/Policies/ICircuitBreaker.cs
using HcmIdentityProvisioning.Domain.Actions;

namespace HcmIdentityProvisioning.Domain.Policies;

public interface ICircuitBreaker
{
    bool ShouldTrip(int totalBatchSize, IReadOnlyList<DeltaAction> proposedActions, out string reason);
}
```

```csharp
// src/HcmIdentityProvisioning.Domain/Policies/ISecurePasswordGenerator.cs
namespace HcmIdentityProvisioning.Domain.Policies;

public interface ISecurePasswordGenerator
{
    string GeneratePassword(int length = 24);
}
```

- [ ] **Step 3: Compilar projeto Domain**

Run: `dotnet build src/HcmIdentityProvisioning.Domain`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 4: Commit**

```bash
git add src/HcmIdentityProvisioning.Domain/
git commit -m "feat(domain): define hexagonal ports (IHcmConnector, IIdentityStore, IRulesEngine) and policies"
```

---

### Task 5: Infrastructure Security & Sanitization (UpnSanitizer, SecurePasswordGenerator)

**Files:**
- Create: `src/HcmIdentityProvisioning.Infrastructure/Security/UpnSanitizer.cs`
- Create: `src/HcmIdentityProvisioning.Infrastructure/Security/SecurePasswordGenerator.cs`
- Test: `tests/HcmIdentityProvisioning.Infrastructure.Tests/Security/UpnSanitizerTests.cs`
- Test: `tests/HcmIdentityProvisioning.Infrastructure.Tests/Security/SecurePasswordGeneratorTests.cs`

**Interfaces:**
- Consumes: `ISecurePasswordGenerator`, `IIdentityStore`, `UserPrincipalName`
- Produces:
  - `UpnSanitizer.SanitizeNameToSlug(string fullName)`
  - `UpnSanitizer.ResolveAvailableUpnAsync(...)`
  - `SecurePasswordGenerator : ISecurePasswordGenerator`

- [ ] **Step 1: Escrever testes que falham para UpnSanitizer e SecurePasswordGenerator**

```csharp
// tests/HcmIdentityProvisioning.Infrastructure.Tests/Security/UpnSanitizerTests.cs
using FluentAssertions;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Domain.ValueObjects;
using HcmIdentityProvisioning.Infrastructure.Security;
using NSubstitute;
using Xunit;

namespace HcmIdentityProvisioning.Infrastructure.Tests.Security;

public class UpnSanitizerTests
{
    [Theory]
    [InlineData("José d'Ávila", "jose.davila")]
    [InlineData("Carlos Eduardo dos Santos", "carlos.santos")]
    [InlineData("Maria João-Gonçalves", "maria.joao-goncalves")]
    [InlineData("Ana  Paula   Menezes", "ana.menezes")]
    public void Sanitize_ShouldStripAccentsAndFormSlug(string fullName, string expectedSlug)
    {
        var slug = UpnSanitizer.SanitizeNameToSlug(fullName);
        slug.Should().Be(expectedSlug);
    }

    [Fact]
    public async Task ResolveAvailableUpnAsync_WhenCollisionExists_AppendsIncrement()
    {
        var store = Substitute.For<IIdentityStore>();
        store.IsUserPrincipalNameAvailableAsync(Arg.Is<UserPrincipalName>(u => u.Value == "john.doe@corp.com"))
            .Returns(false);
        store.IsUserPrincipalNameAvailableAsync(Arg.Is<UserPrincipalName>(u => u.Value == "john.doe2@corp.com"))
            .Returns(true);

        var upn = await UpnSanitizer.ResolveAvailableUpnAsync("john.doe", "corp.com", store);

        upn.Value.Should().Be("john.doe2@corp.com");
    }
}
```

```csharp
// tests/HcmIdentityProvisioning.Infrastructure.Tests/Security/SecurePasswordGeneratorTests.cs
using FluentAssertions;
using HcmIdentityProvisioning.Infrastructure.Security;
using Xunit;

namespace HcmIdentityProvisioning.Infrastructure.Tests.Security;

public class SecurePasswordGeneratorTests
{
    [Fact]
    public void GeneratePassword_ShouldSatisfyComplexityRules()
    {
        var generator = new SecurePasswordGenerator();
        var password = generator.GeneratePassword(24);

        password.Length.Should().Be(24);
        password.Should().MatchRegex(@"[A-Z]");
        password.Should().MatchRegex(@"[a-z]");
        password.Should().MatchRegex(@"[0-9]");
        password.Should().MatchRegex(@"[!@#$%^&*()_+\-=\[\]{}|;:,.<>?]");
    }
}
```

- [ ] **Step 2: Executar testes para verificar falha**

Run: `dotnet test tests/HcmIdentityProvisioning.Infrastructure.Tests --filter FullyQualifiedName~Security`
Expected: FAIL.

- [ ] **Step 3: Implementar UpnSanitizer e SecurePasswordGenerator**

```csharp
// src/HcmIdentityProvisioning.Infrastructure/Security/UpnSanitizer.cs
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Domain.ValueObjects;

namespace HcmIdentityProvisioning.Infrastructure.Security;

public static class UpnSanitizer
{
    public static string SanitizeNameToSlug(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            return "user";

        var normalized = fullName.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }

        var clean = sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant()
            .Replace("'", "").Replace("\"", "");

        var parts = Regex.Split(clean, @"\s+").Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (parts.Count == 0) return "user";
        if (parts.Count == 1) return parts[0];

        return $"{parts.First()}.{parts.Last()}";
    }

    public static async Task<UserPrincipalName> ResolveAvailableUpnAsync(
        string slug,
        string domain,
        IIdentityStore store,
        CancellationToken ct = default)
    {
        var candidate = UserPrincipalName.Create($"{slug}@{domain}").Value;
        if (await store.IsUserPrincipalNameAvailableAsync(candidate, ct))
            return candidate;

        int counter = 2;
        while (counter < 1000)
        {
            var next = UserPrincipalName.Create($"{slug}{counter}@{domain}").Value;
            if (await store.IsUserPrincipalNameAvailableAsync(next, ct))
                return next;
            counter++;
        }

        throw new InvalidOperationException($"Could not allocate available UPN for '{slug}'.");
    }
}
```

```csharp
// src/HcmIdentityProvisioning.Infrastructure/Security/SecurePasswordGenerator.cs
using System.Security.Cryptography;
using HcmIdentityProvisioning.Domain.Policies;

namespace HcmIdentityProvisioning.Infrastructure.Security;

public sealed class SecurePasswordGenerator : ISecurePasswordGenerator
{
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnopqrstuvwxyz";
    private const string Digits = "23456789";
    private const string Special = "!@#$%^&*()_+-=[]{}|;:,.<>?";
    private static readonly string All = Upper + Lower + Digits + Special;

    public string GeneratePassword(int length = 24)
    {
        if (length < 12) length = 12;

        var chars = new char[length];
        chars[0] = Upper[RandomNumberGenerator.GetInt32(Upper.Length)];
        chars[1] = Lower[RandomNumberGenerator.GetInt32(Lower.Length)];
        chars[2] = Digits[RandomNumberGenerator.GetInt32(Digits.Length)];
        chars[3] = Special[RandomNumberGenerator.GetInt32(Special.Length)];

        for (int i = 4; i < length; i++)
        {
            chars[i] = All[RandomNumberGenerator.GetInt32(All.Length)];
        }

        RandomNumberGenerator.Shuffle(chars.AsSpan());
        return new string(chars);
    }
}
```

- [ ] **Step 4: Executar testes para verificar aprovação**

Run: `dotnet test tests/HcmIdentityProvisioning.Infrastructure.Tests --filter FullyQualifiedName~Security`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/HcmIdentityProvisioning.Infrastructure/ tests/HcmIdentityProvisioning.Infrastructure.Tests/
git commit -m "feat(infra): implement UpnSanitizer with diacritic removal and SecurePasswordGenerator"
```

---

### Task 6: Infrastructure Rules Engine Adapter & Default `rules.json`

**Files:**
- Create: `src/HcmIdentityProvisioning.Infrastructure/Rules/MicrosoftRulesEngineAdapter.cs`
- Create: `src/HcmIdentityProvisioning.Infrastructure/Rules/rules.json`
- Test: `tests/HcmIdentityProvisioning.Infrastructure.Tests/Rules/MicrosoftRulesEngineAdapterTests.cs`

**Interfaces:**
- Consumes: `RulesEngine` (5.0.3), `IRulesEngine`, `Employee`
- Produces: `MicrosoftRulesEngineAdapter : IRulesEngine`

- [ ] **Step 1: Adicionar pacote RulesEngine ao projeto Infrastructure**

```powershell
dotnet add src/HcmIdentityProvisioning.Infrastructure package RulesEngine --version 5.0.3
```

- [ ] **Step 2: Criar arquivo de regras padrão `rules.json`**

```json
// src/HcmIdentityProvisioning.Infrastructure/Rules/rules.json
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
      },
      {
        "RuleName": "AllStaffBaseAccess",
        "SuccessEvent": "grp-iam-all-staff",
        "Expression": "Status.ToString() == \"Active\""
      }
    ]
  }
]
```

- [ ] **Step 3: Escrever teste que falha para avaliação de regras**

```csharp
// tests/HcmIdentityProvisioning.Infrastructure.Tests/Rules/MicrosoftRulesEngineAdapterTests.cs
using FluentAssertions;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Enums;
using HcmIdentityProvisioning.Domain.ValueObjects;
using HcmIdentityProvisioning.Infrastructure.Rules;
using Xunit;

namespace HcmIdentityProvisioning.Infrastructure.Tests.Rules;

public class MicrosoftRulesEngineAdapterTests
{
    private const string SampleRulesJson = @"[
      {
        ""WorkflowName"": ""EntraIdGroupAssignment"",
        ""Rules"": [
          {
            ""RuleName"": ""FinanceGroup"",
            ""SuccessEvent"": ""grp-iam-finance"",
            ""Expression"": ""Department == \""Financeiro\""""
          },
          {
            ""RuleName"": ""TechEngineer"",
            ""SuccessEvent"": ""grp-iam-engineering"",
            ""Expression"": ""Department == \""Tecnologia\"" AND JobTitle.Contains(\""Engenheiro\"")""
          }
        ]
      }
    ]";

    [Fact]
    public async Task EvaluateDesiredGroupsAsync_WhenMatchesDepartmentAndRole_ReturnsGroups()
    {
        var adapter = MicrosoftRulesEngineAdapter.FromJsonString(SampleRulesJson);
        var emp = new Employee(
            EmployeeId.Create("101").Value,
            "Lucas Engenheiro",
            EmployeeStatus.Active,
            "Tecnologia",
            "Engenheiro de Software Sênior",
            new Dictionary<string, string>()
        );

        var groups = await adapter.EvaluateDesiredGroupsAsync(emp);

        groups.Should().Contain("grp-iam-engineering");
        groups.Should().NotContain("grp-iam-finance");
    }
}
```

- [ ] **Step 4: Executar teste para verificar falha**

Run: `dotnet test tests/HcmIdentityProvisioning.Infrastructure.Tests --filter FullyQualifiedName~Rules`
Expected: FAIL.

- [ ] **Step 5: Implementar MicrosoftRulesEngineAdapter**

```csharp
// src/HcmIdentityProvisioning.Infrastructure/Rules/MicrosoftRulesEngineAdapter.cs
using System.Text.Json;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Ports;
using RulesEngine.Models;

namespace HcmIdentityProvisioning.Infrastructure.Rules;

public sealed class MicrosoftRulesEngineAdapter : IRulesEngine
{
    private readonly global::RulesEngine.RulesEngine _engine;
    private const string WorkflowName = "EntraIdGroupAssignment";

    public MicrosoftRulesEngineAdapter(Workflow[] workflows)
    {
        _engine = new global::RulesEngine.RulesEngine(workflows);
    }

    public static MicrosoftRulesEngineAdapter FromJsonString(string json)
    {
        var workflows = JsonSerializer.Deserialize<Workflow[]>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? Array.Empty<Workflow>();

        return new MicrosoftRulesEngineAdapter(workflows);
    }

    public static MicrosoftRulesEngineAdapter FromFile(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Rules file not found at '{filePath}'.");

        var json = File.ReadAllText(filePath);
        return FromJsonString(json);
    }

    public async Task<IReadOnlySet<string>> EvaluateDesiredGroupsAsync(Employee employee, CancellationToken ct = default)
    {
        var ruleParams = new RuleParameter[]
        {
            new("Department", employee.Department),
            new("JobTitle", employee.JobTitle),
            new("Status", employee.Status),
            new("FullName", employee.FullName),
            new("ExtendedAttributes", employee.ExtendedAttributes)
        };

        var results = await _engine.ExecuteAllRulesAsync(WorkflowName, ruleParams);
        var matchedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var result in results)
        {
            if (result.IsSuccess && !string.IsNullOrWhiteSpace(result.Rule.SuccessEvent))
            {
                matchedGroups.Add(result.Rule.SuccessEvent);
            }
        }

        return matchedGroups;
    }
}
```

- [ ] **Step 6: Executar testes para verificar aprovação**

Run: `dotnet test tests/HcmIdentityProvisioning.Infrastructure.Tests --filter FullyQualifiedName~Rules`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/HcmIdentityProvisioning.Infrastructure/ tests/HcmIdentityProvisioning.Infrastructure.Tests/
git commit -m "feat(infra): implement MicrosoftRulesEngineAdapter and add rules.json"
```

---

### Task 7: Infrastructure Fixtures, Synthetic Connector & In-Memory Store

**Files:**
- Create: `fixtures/synthetic-employees.json`
- Create: `src/HcmIdentityProvisioning.Infrastructure/Connectors/Synthetic/SyntheticHcmConnector.cs`
- Create: `src/HcmIdentityProvisioning.Infrastructure/Graph/InMemoryIdentityStore.cs`
- Test: `tests/HcmIdentityProvisioning.Infrastructure.Tests/Connectors/SyntheticHcmConnectorTests.cs`
- Test: `tests/HcmIdentityProvisioning.Infrastructure.Tests/Graph/InMemoryIdentityStoreTests.cs`

**Interfaces:**
- Consumes: `IHcmConnector`, `IIdentityStore`, `fixtures/synthetic-employees.json`
- Produces:
  - `SyntheticHcmConnector : IHcmConnector`
  - `InMemoryIdentityStore : IIdentityStore`

- [ ] **Step 1: Criar catálogo de fixtures com os 6 cenários de RH**

```json
// fixtures/synthetic-employees.json
[
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
]
```

- [ ] **Step 2: Escrever testes que falham para o conector e o store**

```csharp
// tests/HcmIdentityProvisioning.Infrastructure.Tests/Connectors/SyntheticHcmConnectorTests.cs
using FluentAssertions;
using HcmIdentityProvisioning.Infrastructure.Connectors.Synthetic;
using Xunit;

namespace HcmIdentityProvisioning.Infrastructure.Tests.Connectors;

public class SyntheticHcmConnectorTests
{
    [Fact]
    public async Task GetEmployeesPageAsync_ReturnsPaginatedResults()
    {
        var connector = SyntheticHcmConnector.FromFixturesFile("fixtures/synthetic-employees.json");
        var page = await connector.GetEmployeesPageAsync(1, 2);

        page.Items.Should().HaveCount(2);
        page.TotalCount.Should().Be(6);
        page.HasNextPage.Should().BeTrue();
    }
}
```

```csharp
// tests/HcmIdentityProvisioning.Infrastructure.Tests/Graph/InMemoryIdentityStoreTests.cs
using FluentAssertions;
using HcmIdentityProvisioning.Domain.Actions;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Enums;
using HcmIdentityProvisioning.Domain.ValueObjects;
using HcmIdentityProvisioning.Infrastructure.Graph;
using Xunit;

namespace HcmIdentityProvisioning.Infrastructure.Tests.Graph;

public class InMemoryIdentityStoreTests
{
    [Fact]
    public async Task ApplyBatchMutationsAsync_CreateAndGroupActions_UpdatesState()
    {
        var store = new InMemoryIdentityStore();
        store.SeedGroup(Guid.NewGuid(), "grp-iam-engineering");

        var emp = new Employee(
            EmployeeId.Create("EMP-001").Value,
            "Carlos Silva",
            EmployeeStatus.Active,
            "Tecnologia",
            "Engenheiro",
            new Dictionary<string, string>()
        );
        var upn = UserPrincipalName.Create("carlos.silva@corp.com").Value;

        var actions = new DeltaAction[]
        {
            new CreateUserAction(emp, upn, "Temp#Pass123456789012")
        };

        await store.ApplyBatchMutationsAsync(actions);

        var users = await store.GetUsersByEmployeeIdsAsync(new[] { emp.Id });
        users.Should().ContainKey(emp.Id);
        users[emp.Id].UserPrincipalName.Value.Should().Be("carlos.silva@corp.com");
    }
}
```

- [ ] **Step 3: Executar testes para verificar falha**

Run: `dotnet test tests/HcmIdentityProvisioning.Infrastructure.Tests --filter FullyQualifiedName~(Synthetic|InMemory)`
Expected: FAIL.

- [ ] **Step 4: Implementar SyntheticHcmConnector e InMemoryIdentityStore**

```csharp
// src/HcmIdentityProvisioning.Infrastructure/Connectors/Synthetic/SyntheticHcmConnector.cs
using System.Text.Json;
using HcmIdentityProvisioning.Domain.Common;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Enums;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Domain.ValueObjects;

namespace HcmIdentityProvisioning.Infrastructure.Connectors.Synthetic;

public sealed class SyntheticHcmConnector : IHcmConnector
{
    private readonly IReadOnlyList<Employee> _employees;

    public SyntheticHcmConnector(IEnumerable<Employee> employees)
    {
        _employees = employees.ToList();
    }

    public static SyntheticHcmConnector FromFixturesFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Fixtures file '{path}' not found.");

        var json = File.ReadAllText(path);
        return FromJson(json);
    }

    public static SyntheticHcmConnector FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<Employee>();

        foreach (var elem in doc.RootElement.EnumerateArray())
        {
            var id = EmployeeId.Create(elem.GetProperty("id").GetString()!).Value;
            var name = elem.GetProperty("fullName").GetString()!;
            var statusStr = elem.GetProperty("status").GetString()!;
            var status = Enum.Parse<EmployeeStatus>(statusStr, ignoreCase: true);
            var dept = elem.GetProperty("department").GetString()!;
            var job = elem.GetProperty("jobTitle").GetString()!;

            var attrs = new Dictionary<string, string>();
            if (elem.TryGetProperty("extendedAttributes", out var attrsElem))
            {
                foreach (var prop in attrsElem.EnumerateObject())
                {
                    attrs[prop.Name] = prop.Value.GetString() ?? "";
                }
            }

            list.Add(new Employee(id, name, status, dept, job, attrs));
        }

        return new SyntheticHcmConnector(list);
    }

    public Task<PagedResult<Employee>> GetEmployeesPageAsync(int pageNumber, int pageSize, CancellationToken ct = default)
    {
        var skip = (pageNumber - 1) * pageSize;
        var pagedItems = _employees.Skip(skip).Take(pageSize).ToList();
        var hasNext = skip + pageSize < _employees.Count;

        return Task.FromResult(new PagedResult<Employee>(
            pagedItems,
            pageNumber,
            pageSize,
            _employees.Count,
            hasNext
        ));
    }
}
```

```csharp
// src/HcmIdentityProvisioning.Infrastructure/Graph/InMemoryIdentityStore.cs
using System.Collections.Concurrent;
using HcmIdentityProvisioning.Domain.Actions;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Domain.ValueObjects;

namespace HcmIdentityProvisioning.Infrastructure.Graph;

public sealed class InMemoryIdentityStore : IIdentityStore
{
    private readonly ConcurrentDictionary<Guid, EntraUser> _usersByGraphId = new();
    private readonly ConcurrentDictionary<string, ManagedGroup> _groupsByName = new(StringComparer.OrdinalIgnoreCase);

    public void SeedGroup(Guid id, string displayName)
    {
        _groupsByName[displayName] = new ManagedGroup(id, displayName);
    }

    public void SeedUser(EntraUser user)
    {
        _usersByGraphId[user.GraphId] = user;
    }

    public Task<IReadOnlyDictionary<EmployeeId, EntraUser>> GetUsersByEmployeeIdsAsync(
        IEnumerable<EmployeeId> employeeIds,
        CancellationToken ct = default)
    {
        var idSet = new HashSet<string>(employeeIds.Select(e => e.Value));
        var matched = _usersByGraphId.Values
            .Where(u => idSet.Contains(u.EmployeeId.Value))
            .ToDictionary(u => u.EmployeeId, u => u);

        return Task.FromResult<IReadOnlyDictionary<EmployeeId, EntraUser>>(matched);
    }

    public Task<bool> IsUserPrincipalNameAvailableAsync(UserPrincipalName upn, CancellationToken ct = default)
    {
        var exists = _usersByGraphId.Values.Any(u => u.UserPrincipalName.Value.Equals(upn.Value, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(!exists);
    }

    public Task<IReadOnlyDictionary<string, ManagedGroup>> GetManagedGroupsAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyDictionary<string, ManagedGroup>>(_groupsByName);
    }

    public Task ApplyBatchMutationsAsync(IEnumerable<DeltaAction> actions, CancellationToken ct = default)
    {
        foreach (var action in actions)
        {
            switch (action)
            {
                case CreateUserAction create:
                    var graphId = Guid.NewGuid();
                    var newUser = new EntraUser(
                        graphId,
                        create.Employee.Id,
                        create.UserPrincipalName,
                        create.Employee.FullName,
                        AccountEnabled: true,
                        AssignedGroupIds: new HashSet<Guid>()
                    );
                    _usersByGraphId[graphId] = newUser;
                    break;

                case UpdateDisplayNameAction updateName:
                    if (_usersByGraphId.TryGetValue(updateName.GraphId, out var existingForName))
                    {
                        _usersByGraphId[updateName.GraphId] = existingForName with { DisplayName = updateName.NewDisplayName };
                    }
                    break;

                case EnableAccountAction enable:
                    if (_usersByGraphId.TryGetValue(enable.GraphId, out var existingForEnable))
                    {
                        _usersByGraphId[enable.GraphId] = existingForEnable with { AccountEnabled = true };
                    }
                    break;

                case DisableAccountAction disable:
                    if (_usersByGraphId.TryGetValue(disable.GraphId, out var existingForDisable))
                    {
                        _usersByGraphId[disable.GraphId] = existingForDisable with { AccountEnabled = false };
                    }
                    break;

                case RevokeSessionsAction:
                    // Simulated in-memory: no-op
                    break;

                case AddGroupMemberAction addGroup:
                    if (_usersByGraphId.TryGetValue(addGroup.GraphId, out var existingForAddGroup))
                    {
                        var groups = new HashSet<Guid>(existingForAddGroup.AssignedGroupIds) { addGroup.GroupId };
                        _usersByGraphId[addGroup.GraphId] = existingForAddGroup with { AssignedGroupIds = groups };
                    }
                    break;

                case RemoveGroupMemberAction removeGroup:
                    if (_usersByGraphId.TryGetValue(removeGroup.GraphId, out var existingForRemoveGroup))
                    {
                        var groups = new HashSet<Guid>(existingForRemoveGroup.AssignedGroupIds);
                        groups.Remove(removeGroup.GroupId);
                        _usersByGraphId[removeGroup.GraphId] = existingForRemoveGroup with { AssignedGroupIds = groups };
                    }
                    break;
            }
        }

        return Task.CompletedTask;
    }
}
```

- [ ] **Step 5: Executar testes para verificar aprovação**

Run: `dotnet test tests/HcmIdentityProvisioning.Infrastructure.Tests --filter FullyQualifiedName~(Synthetic|InMemory)`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add fixtures/ src/HcmIdentityProvisioning.Infrastructure/ tests/HcmIdentityProvisioning.Infrastructure.Tests/
git commit -m "feat(infra): add synthetic employee fixtures, SyntheticHcmConnector and InMemoryIdentityStore"
```

---

### Task 8: Infrastructure Circuit Breaker & Mock Credential Delivery

**Files:**
- Create: `src/HcmIdentityProvisioning.Infrastructure/Policies/DisablementCircuitBreaker.cs`
- Create: `src/HcmIdentityProvisioning.Infrastructure/Notifications/MockCredentialDeliveryService.cs`
- Test: `tests/HcmIdentityProvisioning.Infrastructure.Tests/Policies/CircuitBreakerTests.cs`

**Interfaces:**
- Consumes: `ICircuitBreaker`, `ICredentialDeliveryService`
- Produces:
  - `DisablementCircuitBreaker : ICircuitBreaker`
  - `MockCredentialDeliveryService : ICredentialDeliveryService`

- [ ] **Step 1: Escrever teste que falha para DisablementCircuitBreaker**

```csharp
// tests/HcmIdentityProvisioning.Infrastructure.Tests/Policies/CircuitBreakerTests.cs
using FluentAssertions;
using HcmIdentityProvisioning.Domain.Actions;
using HcmIdentityProvisioning.Infrastructure.Policies;
using Xunit;

namespace HcmIdentityProvisioning.Infrastructure.Tests.Policies;

public class CircuitBreakerTests
{
    [Fact]
    public void ShouldTrip_WhenDisablePercentageExceeded_ReturnsTrue()
    {
        var breaker = new DisablementCircuitBreaker(maxDisablePercentage: 10.0, maxDisableCount: 25);
        var actions = new DeltaAction[]
        {
            new DisableAccountAction(Guid.NewGuid()),
            new DisableAccountAction(Guid.NewGuid())
        };

        var tripped = breaker.ShouldTrip(totalBatchSize: 10, proposedActions: actions, out var reason);

        tripped.Should().BeTrue();
        reason.Should().Contain("Rate 20.00% exceeds threshold 10.00%");
    }

    [Fact]
    public void ShouldTrip_WhenWithinSafeThreshold_ReturnsFalse()
    {
        var breaker = new DisablementCircuitBreaker(maxDisablePercentage: 10.0, maxDisableCount: 25);
        var actions = new DeltaAction[]
        {
            new DisableAccountAction(Guid.NewGuid())
        };

        var tripped = breaker.ShouldTrip(totalBatchSize: 50, proposedActions: actions, out _);

        tripped.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Executar teste para verificar falha**

Run: `dotnet test tests/HcmIdentityProvisioning.Infrastructure.Tests --filter FullyQualifiedName~CircuitBreaker`
Expected: FAIL.

- [ ] **Step 3: Implementar DisablementCircuitBreaker e MockCredentialDeliveryService**

```csharp
// src/HcmIdentityProvisioning.Infrastructure/Policies/DisablementCircuitBreaker.cs
using HcmIdentityProvisioning.Domain.Actions;
using HcmIdentityProvisioning.Domain.Policies;

namespace HcmIdentityProvisioning.Infrastructure.Policies;

public sealed class DisablementCircuitBreaker : ICircuitBreaker
{
    public double MaxDisablePercentage { get; }
    public int MaxDisableCount { get; }

    public DisablementCircuitBreaker(double maxDisablePercentage = 10.0, int maxDisableCount = 25)
    {
        MaxDisablePercentage = maxDisablePercentage;
        MaxDisableCount = maxDisableCount;
    }

    public bool ShouldTrip(int totalBatchSize, IReadOnlyList<DeltaAction> proposedActions, out string reason)
    {
        reason = string.Empty;
        if (totalBatchSize <= 0) return false;

        var disableCount = proposedActions.Count(a => a is DisableAccountAction);
        if (disableCount == 0) return false;

        var disableRate = (double)disableCount / totalBatchSize * 100.0;

        if (disableRate > MaxDisablePercentage)
        {
            reason = $"CRITICAL_AUDIT_BREAKER_TRIPPED: Disablement Rate {disableRate:F2}% exceeds threshold {MaxDisablePercentage:F2}% ({disableCount}/{totalBatchSize} accounts).";
            return true;
        }

        if (disableCount > MaxDisableCount)
        {
            reason = $"CRITICAL_AUDIT_BREAKER_TRIPPED: Disablement Count {disableCount} exceeds absolute limit {MaxDisableCount}.";
            return true;
        }

        return false;
    }
}
```

```csharp
// src/HcmIdentityProvisioning.Infrastructure/Notifications/MockCredentialDeliveryService.cs
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace HcmIdentityProvisioning.Infrastructure.Notifications;

public sealed class MockCredentialDeliveryService : ICredentialDeliveryService
{
    private readonly ILogger<MockCredentialDeliveryService> _logger;

    public MockCredentialDeliveryService(ILogger<MockCredentialDeliveryService> logger)
    {
        _logger = logger;
    }

    public Task DeliverInitialCredentialsAsync(
        Employee employee,
        UserPrincipalName upn,
        string temporaryPassword,
        CancellationToken ct = default)
    {
        if (employee.ExtendedAttributes.TryGetValue("email", out var email) && !string.IsNullOrWhiteSpace(email))
        {
            _logger.LogInformation("Credential dispatch mocked for employee {EmployeeId} ({Upn}) to out-of-band email {Email}", employee.Id, upn, email);
        }
        else
        {
            _logger.LogWarning("CREDENTIAL_DELIVERY_SKIPPED: Employee {EmployeeId} has no out-of-band email", employee.Id);
        }

        return Task.CompletedTask;
    }
}
```

- [ ] **Step 4: Executar testes para verificar aprovação**

Run: `dotnet test tests/HcmIdentityProvisioning.Infrastructure.Tests --filter FullyQualifiedName~CircuitBreaker`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/HcmIdentityProvisioning.Infrastructure/ tests/HcmIdentityProvisioning.Infrastructure.Tests/
git commit -m "feat(infra): add DisablementCircuitBreaker and MockCredentialDeliveryService"
```

---

### Task 9: Application Reconciliation Engine

**Files:**
- Create: `src/HcmIdentityProvisioning.Application/Models/ChangeSet.cs`
- Create: `src/HcmIdentityProvisioning.Application/Models/SyncReport.cs`
- Create: `src/HcmIdentityProvisioning.Application/Options/SyncSettings.cs`
- Create: `src/HcmIdentityProvisioning.Application/Services/IdentityReconciliationService.cs`
- Test: `tests/HcmIdentityProvisioning.Application.Tests/Services/IdentityReconciliationServiceTests.cs`

**Interfaces:**
- Consumes: `Domain` Entities, Ports (`IRulesEngine`, `ISecurePasswordGenerator`)
- Produces: `IdentityReconciliationService.ReconcileEmployeeAsync(...)`

- [ ] **Step 1: Escrever testes que falham para o serviço de reconciliação**

```csharp
// tests/HcmIdentityProvisioning.Application.Tests/Services/IdentityReconciliationServiceTests.cs
using FluentAssertions;
using HcmIdentityProvisioning.Application.Services;
using HcmIdentityProvisioning.Domain.Actions;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Enums;
using HcmIdentityProvisioning.Domain.Policies;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Domain.ValueObjects;
using NSubstitute;
using Xunit;

namespace HcmIdentityProvisioning.Application.Tests.Services;

public class IdentityReconciliationServiceTests
{
    private readonly IRulesEngine _rulesEngine = Substitute.For<IRulesEngine>();
    private readonly ISecurePasswordGenerator _pwdGen = Substitute.For<ISecurePasswordGenerator>();

    public IdentityReconciliationServiceTests()
    {
        _pwdGen.GeneratePassword(Arg.Any<int>()).Returns("MockPassword123!");
    }

    [Fact]
    public async Task Reconcile_WhenJoiner_ProducesCreateActionAndGroupAssignments()
    {
        var service = new IdentityReconciliationService(_rulesEngine, _pwdGen);
        var emp = new Employee(
            EmployeeId.Create("101").Value,
            "Carlos Silva",
            EmployeeStatus.Active,
            "Tecnologia",
            "Engenheiro",
            new Dictionary<string, string>()
        );

        _rulesEngine.EvaluateDesiredGroupsAsync(emp).Returns(new HashSet<string> { "grp-iam-engineering" });

        var groupGuid = Guid.NewGuid();
        var managedGroups = new Dictionary<string, ManagedGroup>(StringComparer.OrdinalIgnoreCase)
        {
            ["grp-iam-engineering"] = new(groupGuid, "grp-iam-engineering")
        };

        var actions = await service.ReconcileEmployeeAsync(
            emp,
            existingUser: null,
            tenantDomain: "corp.com",
            managedGroups: managedGroups,
            isUpnAvailable: _ => Task.FromResult(true)
        );

        actions.Should().ContainSingle(a => a is CreateUserAction);
        actions.Should().ContainSingle(a => a is AddGroupMemberAction);
    }
}
```

- [ ] **Step 2: Executar teste para verificar falha**

Run: `dotnet test tests/HcmIdentityProvisioning.Application.Tests --filter FullyQualifiedName~IdentityReconciliationService`
Expected: FAIL.

- [ ] **Step 3: Implementar Models, Options e IdentityReconciliationService**

```csharp
// src/HcmIdentityProvisioning.Application/Models/ChangeSet.cs
using HcmIdentityProvisioning.Domain.Actions;

namespace HcmIdentityProvisioning.Application.Models;

public sealed record ChangeSet(IReadOnlyList<DeltaAction> Actions)
{
    public static readonly ChangeSet Empty = new(Array.Empty<DeltaAction>());
    public bool IsEmpty => Actions.Count == 0;
}
```

```csharp
// src/HcmIdentityProvisioning.Application/Models/SyncReport.cs
namespace HcmIdentityProvisioning.Application.Models;

public sealed record SyncReport(
    int TotalProcessed,
    int CreatedCount,
    int UpdatedCount,
    int EnabledCount,
    int DisabledCount,
    int SessionsRevokedCount,
    int GroupMembershipsAdded,
    int GroupMembershipsRemoved,
    bool CircuitBreakerTripped,
    string? CircuitBreakerMessage,
    IReadOnlyList<string> Warnings
);
```

```csharp
// src/HcmIdentityProvisioning.Application/Options/SyncSettings.cs
namespace HcmIdentityProvisioning.Application.Options;

public sealed class SyncSettings
{
    public string TenantDomain { get; set; } = "company.onmicrosoft.com";
    public string ManagedGroupPrefix { get; set; } = "grp-iam-";
    public int BatchPageSize { get; set; } = 50;
    public double MaxDisablementPercentage { get; set; } = 10.0;
    public int MaxDisablementCount { get; set; } = 25;
    public bool HaltAllOperationsOnTrip { get; set; } = false;
}
```

```csharp
// src/HcmIdentityProvisioning.Application/Services/IdentityReconciliationService.cs
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HcmIdentityProvisioning.Domain.Actions;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Enums;
using HcmIdentityProvisioning.Domain.Policies;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Domain.ValueObjects;

namespace HcmIdentityProvisioning.Application.Services;

public sealed class IdentityReconciliationService
{
    private readonly IRulesEngine _rulesEngine;
    private readonly ISecurePasswordGenerator _passwordGenerator;

    public IdentityReconciliationService(IRulesEngine rulesEngine, ISecurePasswordGenerator passwordGenerator)
    {
        _rulesEngine = rulesEngine;
        _passwordGenerator = passwordGenerator;
    }

    public async Task<IReadOnlyList<DeltaAction>> ReconcileEmployeeAsync(
        Employee employee,
        EntraUser? existingUser,
        string tenantDomain,
        IReadOnlyDictionary<string, ManagedGroup> managedGroups,
        Func<UserPrincipalName, Task<bool>> isUpnAvailable,
        Action<string>? onWarning = null,
        CancellationToken ct = default)
    {
        var actions = new List<DeltaAction>();
        var desiredGroups = await _rulesEngine.EvaluateDesiredGroupsAsync(employee, ct);

        // 1. Joiner Workflow
        if (existingUser is null)
        {
            if (employee.Status != EmployeeStatus.Active)
                return actions;

            var slug = SanitizeNameToSlug(employee.FullName);
            var upn = await ResolveAvailableUpnAsync(slug, tenantDomain, isUpnAvailable);
            var tempPassword = _passwordGenerator.GeneratePassword(24);

            actions.Add(new CreateUserAction(employee, upn, tempPassword));

            foreach (var groupName in desiredGroups)
            {
                if (managedGroups.TryGetValue(groupName, out var group))
                {
                    actions.Add(new AddGroupMemberAction(Guid.Empty, group.Id, group.DisplayName));
                }
                else
                {
                    onWarning?.Invoke($"WARNING_MANAGED_GROUP_NOT_FOUND: Group '{groupName}' was not found in directory.");
                }
            }

            return actions;
        }

        // 2. Leaver Workflow
        if (employee.Status == EmployeeStatus.Inactive)
        {
            if (existingUser.AccountEnabled)
            {
                actions.Add(new DisableAccountAction(existingUser.GraphId));
                actions.Add(new RevokeSessionsAction(existingUser.GraphId));
            }

            foreach (var managedGroup in managedGroups.Values)
            {
                if (existingUser.AssignedGroupIds.Contains(managedGroup.Id))
                {
                    actions.Add(new RemoveGroupMemberAction(existingUser.GraphId, managedGroup.Id, managedGroup.DisplayName));
                }
            }

            return actions;
        }

        // 3. Mover / Existing Active Workflow
        if (!existingUser.AccountEnabled)
        {
            actions.Add(new EnableAccountAction(existingUser.GraphId));
        }

        if (!string.Equals(existingUser.DisplayName, employee.FullName, StringComparison.Ordinal))
        {
            actions.Add(new UpdateDisplayNameAction(existingUser.GraphId, employee.FullName));
        }

        foreach (var groupName in desiredGroups)
        {
            if (managedGroups.TryGetValue(groupName, out var group))
            {
                if (!existingUser.AssignedGroupIds.Contains(group.Id))
                {
                    actions.Add(new AddGroupMemberAction(existingUser.GraphId, group.Id, group.DisplayName));
                }
            }
            else
            {
                onWarning?.Invoke($"WARNING_MANAGED_GROUP_NOT_FOUND: Group '{groupName}' was not found in directory.");
            }
        }

        foreach (var managedGroup in managedGroups.Values)
        {
            if (existingUser.AssignedGroupIds.Contains(managedGroup.Id) && !desiredGroups.Contains(managedGroup.DisplayName))
            {
                actions.Add(new RemoveGroupMemberAction(existingUser.GraphId, managedGroup.Id, managedGroup.DisplayName));
            }
        }

        return actions;
    }

    private static string SanitizeNameToSlug(string fullName)
    {
        var normalized = fullName.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }

        var clean = sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant()
            .Replace("'", "").Replace("\"", "");
        var parts = Regex.Split(clean, @"\s+").Where(p => !string.IsNullOrWhiteSpace(p)).ToList();

        if (parts.Count == 0) return "user";
        if (parts.Count == 1) return parts[0];
        return $"{parts.First()}.{parts.Last()}";
    }

    private static async Task<UserPrincipalName> ResolveAvailableUpnAsync(
        string slug,
        string domain,
        Func<UserPrincipalName, Task<bool>> isAvailable)
    {
        var baseUpn = UserPrincipalName.Create($"{slug}@{domain}").Value;
        if (await isAvailable(baseUpn)) return baseUpn;

        int counter = 2;
        while (counter < 1000)
        {
            var next = UserPrincipalName.Create($"{slug}{counter}@{domain}").Value;
            if (await isAvailable(next)) return next;
            counter++;
        }

        throw new InvalidOperationException($"Could not allocate available UPN for '{slug}'.");
    }
}
```

- [ ] **Step 4: Executar testes para verificar aprovação**

Run: `dotnet test tests/HcmIdentityProvisioning.Application.Tests --filter FullyQualifiedName~IdentityReconciliationService`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/HcmIdentityProvisioning.Application/ tests/HcmIdentityProvisioning.Application.Tests/
git commit -m "feat(application): implement IdentityReconciliationService with joiner, mover and leaver logic"
```

---

### Task 10: Application Orchestration Use Cases (ReconcileBatchUseCase & DryRunAuditUseCase)

**Files:**
- Create: `src/HcmIdentityProvisioning.Application/UseCases/ReconcileBatchUseCase.cs`
- Create: `src/HcmIdentityProvisioning.Application/UseCases/DryRunAuditUseCase.cs`
- Test: `tests/HcmIdentityProvisioning.Application.Tests/UseCases/ReconcileBatchUseCaseTests.cs`

**Interfaces:**
- Consumes: `IHcmConnector`, `IIdentityStore`, `IdentityReconciliationService`, `ICircuitBreaker`, `ICredentialDeliveryService`
- Produces:
  - `ReconcileBatchUseCase.ExecuteAsync()`
  - `DryRunAuditUseCase.ExecuteAsync()`

- [ ] **Step 1: Escrever teste de circuito aberto para ReconcileBatchUseCase**

```csharp
// tests/HcmIdentityProvisioning.Application.Tests/UseCases/ReconcileBatchUseCaseTests.cs
using FluentAssertions;
using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Application.Services;
using HcmIdentityProvisioning.Application.UseCases;
using HcmIdentityProvisioning.Domain.Common;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Enums;
using HcmIdentityProvisioning.Domain.Policies;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace HcmIdentityProvisioning.Application.Tests.UseCases;

public class ReconcileBatchUseCaseTests
{
    [Fact]
    public async Task ExecuteAsync_WhenBreakerTrips_HaltsDestructiveOperations()
    {
        var connector = Substitute.For<IHcmConnector>();
        var store = Substitute.For<IIdentityStore>();
        var rules = Substitute.For<IRulesEngine>();
        var pwdGen = Substitute.For<ISecurePasswordGenerator>();
        var breaker = Substitute.For<ICircuitBreaker>();
        var delivery = Substitute.For<ICredentialDeliveryService>();

        var emp1 = new Employee(EmployeeId.Create("E1").Value, "User 1", EmployeeStatus.Inactive, "Dep", "Role", new Dictionary<string, string>());
        var paged = new PagedResult<Employee>(new[] { emp1 }, 1, 50, 1, false);
        connector.GetEmployeesPageAsync(1, 50, Arg.Any<CancellationToken>()).Returns(paged);

        var existingUser = new EntraUser(Guid.NewGuid(), emp1.Id, UserPrincipalName.Create("user1@corp.com").Value, "User 1", true, new HashSet<Guid>());
        store.GetUsersByEmployeeIdsAsync(Arg.Any<IEnumerable<EmployeeId>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<EmployeeId, EntraUser> { [emp1.Id] = existingUser });

        breaker.ShouldTrip(Arg.Any<int>(), Arg.Any<IReadOnlyList<Domain.Actions.DeltaAction>>(), out Arg.Any<string>())
            .Returns(x => { x[2] = "Tripped by test"; return true; });

        var reconciler = new IdentityReconciliationService(rules, pwdGen);
        var settings = new SyncSettings { TenantDomain = "corp.com", HaltAllOperationsOnTrip = false };

        var useCase = new ReconcileBatchUseCase(
            connector,
            store,
            reconciler,
            breaker,
            delivery,
            settings,
            NullLogger<ReconcileBatchUseCase>.Instance
        );

        var report = await useCase.ExecuteAsync();

        report.CircuitBreakerTripped.Should().BeTrue();
        report.DisabledCount.Should().Be(0);
        await store.DidNotReceive().ApplyBatchMutationsAsync(Arg.Any<IEnumerable<Domain.Actions.DeltaAction>>(), Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Executar teste para verificar falha**

Run: `dotnet test tests/HcmIdentityProvisioning.Application.Tests --filter FullyQualifiedName~ReconcileBatchUseCase`
Expected: FAIL.

- [ ] **Step 3: Implementar ReconcileBatchUseCase e DryRunAuditUseCase**

```csharp
// src/HcmIdentityProvisioning.Application/UseCases/ReconcileBatchUseCase.cs
using HcmIdentityProvisioning.Application.Models;
using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Application.Services;
using HcmIdentityProvisioning.Domain.Actions;
using HcmIdentityProvisioning.Domain.Policies;
using HcmIdentityProvisioning.Domain.Ports;
using Microsoft.Extensions.Logging;

namespace HcmIdentityProvisioning.Application.UseCases;

public sealed class ReconcileBatchUseCase
{
    private readonly IHcmConnector _connector;
    private readonly IIdentityStore _identityStore;
    private readonly IdentityReconciliationService _reconciler;
    private readonly ICircuitBreaker _circuitBreaker;
    private readonly ICredentialDeliveryService _credentialDelivery;
    private readonly SyncSettings _settings;
    private readonly ILogger<ReconcileBatchUseCase> _logger;

    public ReconcileBatchUseCase(
        IHcmConnector connector,
        IIdentityStore identityStore,
        IdentityReconciliationService reconciler,
        ICircuitBreaker circuitBreaker,
        ICredentialDeliveryService credentialDelivery,
        SyncSettings settings,
        ILogger<ReconcileBatchUseCase> logger)
    {
        _connector = connector;
        _identityStore = identityStore;
        _reconciler = reconciler;
        _circuitBreaker = circuitBreaker;
        _credentialDelivery = credentialDelivery;
        _settings = settings;
        _logger = logger;
    }

    public async Task<SyncReport> ExecuteAsync(CancellationToken ct = default)
    {
        int page = 1;
        bool hasNext = true;
        var warnings = new List<string>();

        int total = 0, created = 0, updated = 0, enabled = 0, disabled = 0, revoked = 0, grpAdded = 0, grpRemoved = 0;
        bool tripped = false;
        string? breakerMsg = null;

        var managedGroups = await _identityStore.GetManagedGroupsAsync(ct);

        while (hasNext)
        {
            var paged = await _connector.GetEmployeesPageAsync(page, _settings.BatchPageSize, ct);
            total += paged.Items.Count;

            var existingUsers = await _identityStore.GetUsersByEmployeeIdsAsync(paged.Items.Select(e => e.Id), ct);
            var batchActions = new List<DeltaAction>();

            foreach (var emp in paged.Items)
            {
                existingUsers.TryGetValue(emp.Id, out var existing);
                var actions = await _reconciler.ReconcileEmployeeAsync(
                    emp,
                    existing,
                    _settings.TenantDomain,
                    managedGroups,
                    upn => _identityStore.IsUserPrincipalNameAvailableAsync(upn, ct),
                    w => warnings.Add(w),
                    ct
                );
                batchActions.AddRange(actions);
            }

            if (_circuitBreaker.ShouldTrip(paged.Items.Count, batchActions, out var reason))
            {
                tripped = true;
                breakerMsg = reason;
                _logger.LogCritical("CIRCUIT BREAKER TRIPPED: {Reason}", reason);

                if (_settings.HaltAllOperationsOnTrip)
                {
                    break;
                }

                batchActions.RemoveAll(a => a is DisableAccountAction or RevokeSessionsAction);
            }

            if (batchActions.Count > 0)
            {
                await _identityStore.ApplyBatchMutationsAsync(batchActions, ct);

                foreach (var action in batchActions)
                {
                    switch (action)
                    {
                        case CreateUserAction c:
                            created++;
                            await _credentialDelivery.DeliverInitialCredentialsAsync(c.Employee, c.UserPrincipalName, c.TemporaryPassword, ct);
                            break;
                        case UpdateDisplayNameAction: updated++; break;
                        case EnableAccountAction: enabled++; break;
                        case DisableAccountAction: disabled++; break;
                        case RevokeSessionsAction: revoked++; break;
                        case AddGroupMemberAction: grpAdded++; break;
                        case RemoveGroupMemberAction: grpRemoved++; break;
                    }
                }
            }

            hasNext = paged.HasNextPage;
            page++;
        }

        return new SyncReport(
            total, created, updated, enabled, disabled, revoked, grpAdded, grpRemoved,
            tripped, breakerMsg, warnings
        );
    }
}
```

```csharp
// src/HcmIdentityProvisioning.Application/UseCases/DryRunAuditUseCase.cs
using HcmIdentityProvisioning.Application.Models;
using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Application.Services;
using HcmIdentityProvisioning.Domain.Actions;
using HcmIdentityProvisioning.Domain.Policies;
using HcmIdentityProvisioning.Domain.Ports;
using Microsoft.Extensions.Logging;

namespace HcmIdentityProvisioning.Application.UseCases;

public sealed class DryRunAuditUseCase
{
    private readonly IHcmConnector _connector;
    private readonly IIdentityStore _identityStore;
    private readonly IdentityReconciliationService _reconciler;
    private readonly ICircuitBreaker _circuitBreaker;
    private readonly SyncSettings _settings;
    private readonly ILogger<DryRunAuditUseCase> _logger;

    public DryRunAuditUseCase(
        IHcmConnector connector,
        IIdentityStore identityStore,
        IdentityReconciliationService reconciler,
        ICircuitBreaker circuitBreaker,
        SyncSettings settings,
        ILogger<DryRunAuditUseCase> logger)
    {
        _connector = connector;
        _identityStore = identityStore;
        _reconciler = reconciler;
        _circuitBreaker = circuitBreaker;
        _settings = settings;
        _logger = logger;
    }

    public async Task<SyncReport> ExecuteAsync(CancellationToken ct = default)
    {
        int page = 1;
        bool hasNext = true;
        var warnings = new List<string>();

        int total = 0, created = 0, updated = 0, enabled = 0, disabled = 0, revoked = 0, grpAdded = 0, grpRemoved = 0;
        bool tripped = false;
        string? breakerMsg = null;

        var managedGroups = await _identityStore.GetManagedGroupsAsync(ct);

        while (hasNext)
        {
            var paged = await _connector.GetEmployeesPageAsync(page, _settings.BatchPageSize, ct);
            total += paged.Items.Count;

            var existingUsers = await _identityStore.GetUsersByEmployeeIdsAsync(paged.Items.Select(e => e.Id), ct);
            var batchActions = new List<DeltaAction>();

            foreach (var emp in paged.Items)
            {
                existingUsers.TryGetValue(emp.Id, out var existing);
                var actions = await _reconciler.ReconcileEmployeeAsync(
                    emp,
                    existing,
                    _settings.TenantDomain,
                    managedGroups,
                    upn => _identityStore.IsUserPrincipalNameAvailableAsync(upn, ct),
                    w => warnings.Add(w),
                    ct
                );
                batchActions.AddRange(actions);
            }

            if (_circuitBreaker.ShouldTrip(paged.Items.Count, batchActions, out var reason))
            {
                tripped = true;
                breakerMsg = reason;
            }

            foreach (var action in batchActions)
            {
                switch (action)
                {
                    case CreateUserAction: created++; break;
                    case UpdateDisplayNameAction: updated++; break;
                    case EnableAccountAction: enabled++; break;
                    case DisableAccountAction: disabled++; break;
                    case RevokeSessionsAction: revoked++; break;
                    case AddGroupMemberAction: grpAdded++; break;
                    case RemoveGroupMemberAction: grpRemoved++; break;
                }
            }

            hasNext = paged.HasNextPage;
            page++;
        }

        return new SyncReport(
            total, created, updated, enabled, disabled, revoked, grpAdded, grpRemoved,
            tripped, breakerMsg, warnings
        );
    }
}
```

- [ ] **Step 4: Executar testes para verificar aprovação**

Run: `dotnet test tests/HcmIdentityProvisioning.Application.Tests --filter FullyQualifiedName~ReconcileBatchUseCase`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/HcmIdentityProvisioning.Application/ tests/HcmIdentityProvisioning.Application.Tests/
git commit -m "feat(application): implement ReconcileBatchUseCase and DryRunAuditUseCase"
```

---

### Task 11: CLI Console Application (System.CommandLine, Commands & DI Sandbox)

**Files:**
- Create: `src/HcmIdentityProvisioning.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Create: `src/HcmIdentityProvisioning.Cli/Commands/SyncCommand.cs`
- Create: `src/HcmIdentityProvisioning.Cli/Commands/ValidateRulesCommand.cs`
- Create: `src/HcmIdentityProvisioning.Cli/Program.cs`

**Interfaces:**
- Consumes: `System.CommandLine`, `ReconcileBatchUseCase`, `DryRunAuditUseCase`, `SyntheticHcmConnector`, `InMemoryIdentityStore`
- Produces: Binário executável do CLI (`hcm-sync`) com comandos `sync` e `validate-rules`.

- [ ] **Step 1: Adicionar System.CommandLine e extensões de DI**

```powershell
dotnet add src/HcmIdentityProvisioning.Infrastructure package Microsoft.Extensions.DependencyInjection.Abstractions --version 10.0.0-preview.1.25080.5
dotnet add src/HcmIdentityProvisioning.Cli package System.CommandLine --version 2.0.0-beta4.22272.1
dotnet add src/HcmIdentityProvisioning.Cli package Microsoft.Extensions.Logging.Console --version 10.0.0-preview.1.25080.5
```

- [ ] **Step 2: Implementar ServiceCollectionExtensions em Infrastructure**

```csharp
// src/HcmIdentityProvisioning.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs
using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Application.Services;
using HcmIdentityProvisioning.Application.UseCases;
using HcmIdentityProvisioning.Domain.Policies;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Infrastructure.Connectors.Synthetic;
using HcmIdentityProvisioning.Infrastructure.Graph;
using HcmIdentityProvisioning.Infrastructure.Notifications;
using HcmIdentityProvisioning.Infrastructure.Policies;
using HcmIdentityProvisioning.Infrastructure.Rules;
using HcmIdentityProvisioning.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;

namespace HcmIdentityProvisioning.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddHcmProvisioningCore(
        this IServiceCollection services,
        SyncSettings settings,
        string rulesJsonPath)
    {
        services.AddSingleton(settings);
        services.AddSingleton<ISecurePasswordGenerator, SecurePasswordGenerator>();
        services.AddSingleton<ICircuitBreaker>(_ => new DisablementCircuitBreaker(settings.MaxDisablementPercentage, settings.MaxDisablementCount));
        services.AddSingleton<IRulesEngine>(_ => MicrosoftRulesEngineAdapter.FromFile(rulesJsonPath));
        services.AddSingleton<IdentityReconciliationService>();
        services.AddTransient<ReconcileBatchUseCase>();
        services.AddTransient<DryRunAuditUseCase>();

        return services;
    }

    public static IServiceCollection AddSyntheticHcmConnector(
        this IServiceCollection services,
        string fixturesPath)
    {
        services.AddSingleton<IHcmConnector>(_ => SyntheticHcmConnector.FromFixturesFile(fixturesPath));
        return services;
    }

    public static IServiceCollection AddInMemoryIdentityStore(
        this IServiceCollection services,
        Action<InMemoryIdentityStore>? seedAction = null)
    {
        var store = new InMemoryIdentityStore();
        seedAction?.Invoke(store);
        services.AddSingleton<IIdentityStore>(store);
        services.AddSingleton<ICredentialDeliveryService, MockCredentialDeliveryService>();
        return services;
    }
}
```

- [ ] **Step 3: Implementar comandos do CLI e Program.cs**

```csharp
// src/HcmIdentityProvisioning.Cli/Commands/ValidateRulesCommand.cs
using System.CommandLine;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Enums;
using HcmIdentityProvisioning.Domain.ValueObjects;
using HcmIdentityProvisioning.Infrastructure.Rules;

namespace HcmIdentityProvisioning.Cli.Commands;

public static class ValidateRulesCommand
{
    public static Command Create()
    {
        var rulesFileOption = new Option<FileInfo>(
            name: "--rules",
            description: "Path to rules.json file.",
            getDefaultValue: () => new FileInfo("src/HcmIdentityProvisioning.Infrastructure/Rules/rules.json"));

        var cmd = new Command("validate-rules", "Validates the syntax and evaluability of rules.json")
        {
            rulesFileOption
        };

        cmd.SetHandler(async (FileInfo file) =>
        {
            if (!file.Exists)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: Rules file '{file.FullName}' not found.");
                Console.ResetColor();
                return;
            }

            try
            {
                var adapter = MicrosoftRulesEngineAdapter.FromFile(file.FullName);
                var probeEmployee = new Employee(
                    EmployeeId.Create("TEST-01").Value,
                    "Probe Employee",
                    EmployeeStatus.Active,
                    "Tecnologia",
                    "Engenheiro",
                    new Dictionary<string, string>()
                );

                var groups = await adapter.EvaluateDesiredGroupsAsync(probeEmployee);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"SUCCESS: Rules syntax valid! Evaluated sample probe employee into {groups.Count} group(s): [{string.Join(", ", groups)}]");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"FAILURE: Rules evaluation failed: {ex.Message}");
                Console.ResetColor();
            }
        }, rulesFileOption);

        return cmd;
    }
}
```

```csharp
// src/HcmIdentityProvisioning.Cli/Commands/SyncCommand.cs
using System.CommandLine;
using System.Text.Json;
using HcmIdentityProvisioning.Application.Models;
using HcmIdentityProvisioning.Application.UseCases;
using Microsoft.Extensions.DependencyInjection;

namespace HcmIdentityProvisioning.Cli.Commands;

public static class SyncCommand
{
    public static Command Create(IServiceProvider serviceProvider)
    {
        var dryRunOption = new Option<bool>(
            name: "--dry-run",
            description: "Execute reconciliation in audit mode without committing changes.");

        var jsonLogsOption = new Option<bool>(
            name: "--json-logs",
            description: "Output audit report as structured NDJSON for SIEM ingestion.");

        var cmd = new Command("sync", "Executes HCM to Entra ID identity lifecycle synchronization.")
        {
            dryRunOption,
            jsonLogsOption
        };

        cmd.SetHandler(async (bool dryRun, bool jsonLogs) =>
        {
            using var scope = serviceProvider.CreateScope();
            SyncReport report;

            if (dryRun)
            {
                var useCase = scope.ServiceProvider.GetRequiredService<DryRunAuditUseCase>();
                report = await useCase.ExecuteAsync();
            }
            else
            {
                var useCase = scope.ServiceProvider.GetRequiredService<ReconcileBatchUseCase>();
                report = await useCase.ExecuteAsync();
            }

            if (jsonLogs)
            {
                Console.WriteLine(JsonSerializer.Serialize(report));
            }
            else
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("=================================================");
                Console.WriteLine($"  HCM IDENTITY RECONCILIATION REPORT (DryRun={dryRun})");
                Console.WriteLine("=================================================");
                Console.ResetColor();
                Console.WriteLine($"Total Employees Processed:      {report.TotalProcessed}");
                Console.WriteLine($"Users Created (Joiners):        {report.CreatedCount}");
                Console.WriteLine($"Profiles Updated:               {report.UpdatedCount}");
                Console.WriteLine($"Accounts Enabled:               {report.EnabledCount}");
                Console.WriteLine($"Accounts Disabled (Leavers):    {report.DisabledCount}");
                Console.WriteLine($"Sessions Revoked:               {report.SessionsRevokedCount}");
                Console.WriteLine($"Group Memberships Added:        {report.GroupMembershipsAdded}");
                Console.WriteLine($"Group Memberships Removed:      {report.GroupMembershipsRemoved}");

                if (report.CircuitBreakerTripped)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"CIRCUIT BREAKER: TRIPPED! -> {report.CircuitBreakerMessage}");
                    Console.ResetColor();
                }

                if (report.Warnings.Count > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("\nWarnings:");
                    foreach (var w in report.Warnings)
                    {
                        Console.WriteLine($" - {w}");
                    }
                    Console.ResetColor();
                }
                Console.WriteLine();
            }
        }, dryRunOption, jsonLogsOption);

        return cmd;
    }
}
```

```csharp
// src/HcmIdentityProvisioning.Cli/Program.cs
using System.CommandLine;
using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Cli.Commands;
using HcmIdentityProvisioning.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var services = new ServiceCollection();
services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning));

var settings = new SyncSettings
{
    TenantDomain = "company.onmicrosoft.com",
    ManagedGroupPrefix = "grp-iam-"
};

var rulesPath = Path.Combine(AppContext.BaseDirectory, "rules.json");
if (!File.Exists(rulesPath))
{
    rulesPath = "src/HcmIdentityProvisioning.Infrastructure/Rules/rules.json";
}

var fixturesPath = "fixtures/synthetic-employees.json";

services.AddHcmProvisioningCore(settings, rulesPath);
services.AddSyntheticHcmConnector(fixturesPath);
services.AddInMemoryIdentityStore(store =>
{
    store.SeedGroup(Guid.Parse("11111111-1111-1111-1111-111111111111"), "grp-iam-engineering");
    store.SeedGroup(Guid.Parse("22222222-2222-2222-2222-222222222222"), "grp-iam-finance");
    store.SeedGroup(Guid.Parse("33333333-3333-3333-3333-333333333333"), "grp-iam-all-staff");
});

var sp = services.BuildServiceProvider();

var rootCommand = new RootCommand("HCM to Microsoft Entra ID Provisioning & Lifecycle Engine");
rootCommand.AddCommand(SyncCommand.Create(sp));
rootCommand.AddCommand(ValidateRulesCommand.Create());

return await rootCommand.InvokeAsync(args);
```

- [ ] **Step 4: Executar CLI em modo audit / dry-run**

Run: `dotnet run --project src/HcmIdentityProvisioning.Cli -- sync --dry-run`
Expected: Saída visual com relatório exibindo 6 colaboradores processados, joiners contabilizados e zero erros.

- [ ] **Step 5: Commit**

```bash
git add src/HcmIdentityProvisioning.Infrastructure/ src/HcmIdentityProvisioning.Cli/
git commit -m "feat(cli): implement CLI sandbox runner with sync, dry-run, json-logs and validate-rules"
```

---

### Task 12: Cycle 1 End-to-End Verification (6 Scenarios & Idempotency)

**Files:**
- Create: `tests/HcmIdentityProvisioning.Infrastructure.Tests/EndToEnd/FullLifecycleIntegrationTests.cs`

**Interfaces:**
- Consumes: Todos os componentes do Ciclo 1 montados
- Produces: Teste de integração ponta a ponta verificando Joiner, Mover, Leaver, Colisão, Diacríticos, Idempotência e execução sub-segundo (< 1s).

- [ ] **Step 1: Escrever teste de integração de ponta a ponta**

```csharp
// tests/HcmIdentityProvisioning.Infrastructure.Tests/EndToEnd/FullLifecycleIntegrationTests.cs
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

        // 1. Desempenho sub-segundo (< 1000ms)
        sw.ElapsedMilliseconds.Should().BeLessThan(1000);

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
```

- [ ] **Step 2: Executar todos os testes da solução**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: >12, Skipped: 0`

- [ ] **Step 3: Commit**

```bash
git add tests/HcmIdentityProvisioning.Infrastructure.Tests/
git commit -m "test(integration): add FullLifecycleIntegrationTests verifying all 6 scenarios and idempotency"
```
