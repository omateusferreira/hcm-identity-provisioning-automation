# Microsoft Graph API & SDK v5 - Referências e Documentação Oficial

> **Instruções:** Cole aqui as URLs da documentação oficial da Microsoft (Microsoft Learn), snippets, anotações ou referências para cada operação. O objetivo é servir como base e contexto técnico para a implementação do `EntraIdGraphAdapter`.

---

### 1. Autenticação & Configuração do SDK v5
- **Finalidade:** Inicialização do `GraphServiceClient` com `DefaultAzureCredential` (.NET 10 / Azure.Identity / App-only).
- **Documentação / URLs / Notas:**  
  https://learn.microsoft.com/en-us/graph/sdks/sdks-overview

  https://learn.microsoft.com/en-us/graph/sdks/sdk-installation

  https://learn.microsoft.com/en-us/graph/sdks/create-client?tabs=csharp

  https://learn.microsoft.com/en-us/graph/sdks/customize-client?tabs=csharp

  https://learn.microsoft.com/en-us/graph/sdks/choose-authentication-providers?tabs=csharp

  https://learn.microsoft.com/en-us/graph/sdks/create-requests?tabs=csharp

  https://learn.microsoft.com/en-us/graph/sdks/paging?tabs=csharp

  https://learn.microsoft.com/en-us/graph/sdks/batch-requests?tabs=csharp

---

### 2. Criação de Usuário (Joiner)
- **Finalidade:** `POST /users` com dados básicos, `employeeId` e perfil de senha temporária forçando alteração no primeiro login.
- **Documentação / URLs / Notas:**  
  https://learn.microsoft.com/en-us/graph/api/user-post-users?view=graph-rest-1.0&WT.mc_id=msgraph_inproduct_graphexhelp&tabs=http

---

### 3. Atualização de Perfil e Estado da Conta (Mover / Leaver)
- **Finalidade:** `PATCH /users/{id}` para atualizar `displayName` ou alterar `accountEnabled` (`true`/`false`).
- **Documentação / URLs / Notas:**  
  https://learn.microsoft.com/en-us/graph/api/user-update?view=graph-rest-1.0&tabs=http

---

### 4. Consulta em Lote de Usuários por `employeeId`
- **Finalidade:** `GET /users` filtrando por lista de `employeeId`, recuperando atributos e grupos aos quais o usuário pertence.
- **Documentação / URLs / Notas:**  
  https://learn.microsoft.com/en-us/graph/api/resources/users?view=graph-rest-1.0

  https://learn.microsoft.com/en-us/graph/api/user-list?view=graph-rest-1.0&tabs=http
---

### 5. Verificação de Disponibilidade de UPN
- **Finalidade:** Consultar se um determinado `UserPrincipalName` já está em uso no diretório.
- **Documentação / URLs / Notas:**  
  https://learn.microsoft.com/en-us/graph/api/resources/users?view=graph-rest-1.0

  https://learn.microsoft.com/en-us/graph/api/user-list?view=graph-rest-1.0&tabs=http

  Para verificar se um User Principal Name (UPN) já está em uso no Microsoft Entra ID usando a Microsoft Graph API, a melhor requisição é um GET filtrado no endpoint /users.
  Esta consulta é extremamente leve (consome o mínimo de Resource Units) e retorna uma lista vazia se o UPN estiver disponível, ou os dados do usuário caso já esteja ocupado.
  Use o operador $filter especificando o UPN exato. Adicione também o parâmetro $select=id para que o Entra ID retorne apenas o ID do usuário (caso ele exista), reduzindo drasticamente o tamanho do payload e economizando performance.
---

### 6. Listagem de Grupos Gerenciados
- **Finalidade:** `GET /groups` filtrando grupos pelo prefixo de governança (`grp-iam-*`).
- **Documentação / URLs / Notas:**  
  https://learn.microsoft.com/en-us/graph/api/group-list?view=graph-rest-1.0&tabs=http

  https://learn.microsoft.com/en-us/graph/api/group-get?view=graph-rest-1.0&tabs=http
---

### 7. Gestão de Membros de Grupos (Adicionar e Remover)
- **Finalidade:** `POST /groups/{groupId}/members/$ref` e `DELETE /groups/{groupId}/members/{userId}/$ref`.
- **Documentação / URLs / Notas:**  
  Remover: https://learn.microsoft.com/en-us/graph/api/group-delete-members?view=graph-rest-1.0&tabs=http

  Adicionar: https://learn.microsoft.com/en-us/graph/api/group-post-members?view=graph-rest-1.0&tabs=http
---

### 8. Revogação de Sessões de Login (Leaver)
- **Finalidade:** `POST /users/{id}/revokeSignInSessions` para invalidar tokens e sessões ativas imediatamente.
- **Documentação / URLs / Notas:**  
  https://learn.microsoft.com/en-us/graph/api/user-revokesigninsessions?view=graph-rest-1.0&tabs=http

---

### 9. Operações em Lote JSON (`$batch` API) & SDK v5
- **Finalidade:** Envio de até 20 requisições por payload via `BatchRequestContentCollection` e leitura das respostas individuais.
- **Documentação / URLs / Notas:**  
  https://learn.microsoft.com/en-us/graph/json-batching?tabs=http

---

### 10. Throttling (HTTP 429), Retry-After e Tratamento de Erros
- **Finalidade:** Boas práticas de resiliência e tratamento de exceções no Microsoft Graph SDK v5.
- **Documentação / URLs / Notas:**  
  https://learn.microsoft.com/en-us/graph/throttling

  https://learn.microsoft.com/en-us/graph/throttling-limits

---

### 11. Links e Referências Gerais / Outros
- **Documentação / URLs / Notas:**  

  
