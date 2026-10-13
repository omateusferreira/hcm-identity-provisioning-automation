using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HcmIdentityProvisioning.Domain.Common;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Enums;
using HcmIdentityProvisioning.Domain.ValueObjects;
using HcmIdentityProvisioning.Infrastructure.Connectors.Rest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace HcmIdentityProvisioning.Infrastructure.Tests.Connectors.Rest;

public sealed class GenericRestHcmConnectorTests
{
    private const string SampleStandardJson = """
    {
      "items": [
        {
          "id": "EMP-001",
          "fullName": "João Silva",
          "status": "Active",
          "department": "Tecnologia",
          "jobTitle": "Engenheiro de Software",
          "extendedAttributes": {
            "email": "joao.silva@personal.com",
            "costCenter": "TI-01"
          }
        },
        {
          "id": "EMP-002",
          "fullName": "Maria Santos",
          "status": "Inactive",
          "department": "Recursos Humanos",
          "jobTitle": "Analista de RH",
          "extendedAttributes": {
            "email": "maria.santos@personal.com"
          }
        }
      ],
      "pageNumber": 1,
      "pageSize": 2,
      "totalCount": 10
    }
    """;

    [Fact]
    public async Task GetEmployeesPageAsync_WithDefaultMapper_DeserializesStandardEnvelopeCorrectly()
    {
        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var handler = new TestHttpMessageHandler((req, _) =>
        {
            capturedRequest = req;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SampleStandardJson, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        });

        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.hcm.example.com")
        };

        var options = new RestHcmConnectorOptions
        {
            BaseUrl = "https://api.hcm.example.com",
            Endpoint = "/api/v1/employees"
        };

        var mapper = new DefaultHcmPayloadMapper();
        var connector = new GenericRestHcmConnector(httpClient, mapper, options);

        // Act
        var result = await connector.GetEmployeesPageAsync(pageNumber: 1, pageSize: 2);

        // Assert
        capturedRequest.Should().NotBeNull();
        capturedRequest!.RequestUri!.PathAndQuery.Should().Be("/api/v1/employees?pageNumber=1&pageSize=2");

        result.Should().NotBeNull();
        result.PageNumber.Should().Be(1);
        result.PageSize.Should().Be(2);
        result.TotalCount.Should().Be(10);
        result.HasNextPage.Should().BeTrue();
        result.Items.Should().HaveCount(2);

        var first = result.Items[0];
        first.Id.Value.Should().Be("EMP-001");
        first.FullName.Should().Be("João Silva");
        first.Status.Should().Be(EmployeeStatus.Active);
        first.Department.Should().Be("Tecnologia");
        first.JobTitle.Should().Be("Engenheiro de Software");
        first.ExtendedAttributes["email"].Should().Be("joao.silva@personal.com");
        first.ExtendedAttributes["costCenter"].Should().Be("TI-01");

        var second = result.Items[1];
        second.Id.Value.Should().Be("EMP-002");
        second.FullName.Should().Be("Maria Santos");
        second.Status.Should().Be(EmployeeStatus.Inactive);
        second.Department.Should().Be("Recursos Humanos");
        second.JobTitle.Should().Be("Analista de RH");
    }

    [Fact]
    public async Task GetEmployeesPageAsync_WithBearerAuth_AddsAuthorizationBearerHeader()
    {
        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var handler = new TestHttpMessageHandler((req, _) =>
        {
            capturedRequest = req;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SampleStandardJson, Encoding.UTF8, "application/json")
            });
        });

        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.hcm.example.com")
        };

        var options = new RestHcmConnectorOptions
        {
            AuthScheme = HcmAuthScheme.Bearer,
            BearerToken = "test-token-bearer-123",
            Endpoint = "/api/v1/employees"
        };

        var connector = new GenericRestHcmConnector(httpClient, new DefaultHcmPayloadMapper(), options);

        // Act
        await connector.GetEmployeesPageAsync(1, 10);

        // Assert
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Headers.Authorization.Should().NotBeNull();
        capturedRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");
        capturedRequest.Headers.Authorization.Parameter.Should().Be("test-token-bearer-123");
    }

    [Fact]
    public async Task GetEmployeesPageAsync_WithApiKeyAuth_AddsConfiguredApiKeyHeader()
    {
        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var handler = new TestHttpMessageHandler((req, _) =>
        {
            capturedRequest = req;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SampleStandardJson, Encoding.UTF8, "application/json")
            });
        });

        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.hcm.example.com")
        };

        var options = new RestHcmConnectorOptions
        {
            AuthScheme = HcmAuthScheme.ApiKey,
            ApiKey = "api-key-xyz-789",
            ApiKeyHeaderName = "X-HCM-Key",
            Endpoint = "/api/v1/employees"
        };

        var connector = new GenericRestHcmConnector(httpClient, new DefaultHcmPayloadMapper(), options);

        // Act
        await connector.GetEmployeesPageAsync(1, 10);

        // Assert
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Headers.Contains("X-HCM-Key").Should().BeTrue();
        capturedRequest.Headers.GetValues("X-HCM-Key").Should().ContainSingle().Which.Should().Be("api-key-xyz-789");
    }

    [Fact]
    public async Task GetEmployeesPageAsync_WithCustomHeaders_IncludesCustomHeadersInRequest()
    {
        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var handler = new TestHttpMessageHandler((req, _) =>
        {
            capturedRequest = req;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SampleStandardJson, Encoding.UTF8, "application/json")
            });
        });

        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.hcm.example.com")
        };

        var options = new RestHcmConnectorOptions
        {
            AuthScheme = HcmAuthScheme.None,
            Endpoint = "/api/v1/employees",
            CustomHeaders = new Dictionary<string, string>
            {
                ["X-Tenant-ID"] = "tenant-enterprise-42",
                ["X-Correlation-ID"] = "corr-abc-123"
            }
        };

        var connector = new GenericRestHcmConnector(httpClient, new DefaultHcmPayloadMapper(), options);

        // Act
        await connector.GetEmployeesPageAsync(1, 10);

        // Assert
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Headers.Contains("X-Tenant-ID").Should().BeTrue();
        capturedRequest.Headers.GetValues("X-Tenant-ID").Should().ContainSingle().Which.Should().Be("tenant-enterprise-42");
        capturedRequest.Headers.Contains("X-Correlation-ID").Should().BeTrue();
        capturedRequest.Headers.GetValues("X-Correlation-ID").Should().ContainSingle().Which.Should().Be("corr-abc-123");
    }

    [Fact]
    public async Task GetEmployeesPageAsync_WithCustomMapper_UsesCustomRoutingAndMapping()
    {
        // Arrange
        const string customErpJson = """
        {
          "colaboradores": [
            {
              "matricula": "ERP-999",
              "nome": "Fernando Souza",
              "ativo": true,
              "area": "Operações",
              "posicao": "Supervisor",
              "detalhes": {
                "email": "fernando@custom-erp.com"
              }
            }
          ],
          "indicePagina": 3,
          "limite": 1,
          "contagemTotal": 50
        }
        """;

        HttpRequestMessage? capturedRequest = null;
        var handler = new TestHttpMessageHandler((req, _) =>
        {
            capturedRequest = req;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(customErpJson, Encoding.UTF8, "application/json")
            });
        });

        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://legacy-erp.local")
        };

        var options = new RestHcmConnectorOptions
        {
            Endpoint = "/v2/funcionarios"
        };

        var customMapper = new CustomErpPayloadMapper();
        var connector = new GenericRestHcmConnector(httpClient, customMapper, options);

        // Act
        var result = await connector.GetEmployeesPageAsync(pageNumber: 3, pageSize: 1);

        // Assert
        capturedRequest.Should().NotBeNull();
        capturedRequest!.RequestUri!.PathAndQuery.Should().Be("/v2/funcionarios/export?offset=3&count=1");

        result.Should().NotBeNull();
        result.PageNumber.Should().Be(3);
        result.PageSize.Should().Be(1);
        result.TotalCount.Should().Be(50);
        result.HasNextPage.Should().BeTrue();
        result.Items.Should().ContainSingle();

        var emp = result.Items[0];
        emp.Id.Value.Should().Be("ERP-999");
        emp.FullName.Should().Be("Fernando Souza");
        emp.Status.Should().Be(EmployeeStatus.Active);
        emp.Department.Should().Be("Operações");
        emp.JobTitle.Should().Be("Supervisor");
        emp.ExtendedAttributes["email"].Should().Be("fernando@custom-erp.com");
    }

    [Fact]
    public async Task GetEmployeesPageAsync_WithTransient503Error_AutomaticallyRetriesViaResiliencePolicy()
    {
        // Arrange
        var requestCount = 0;
        var handler = new TestHttpMessageHandler((_, _) =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SampleStandardJson, Encoding.UTF8, "application/json")
            });
        });

        var services = new ServiceCollection();
        var clientBuilder = services.AddHttpClient("ResilientHcmClient", client =>
        {
            client.BaseAddress = new Uri("https://api.hcm.example.com");
        });

        clientBuilder.ConfigurePrimaryHttpMessageHandler(() => handler);

        clientBuilder.AddStandardResilienceHandler(options =>
        {
            options.Retry.BackoffType = DelayBackoffType.Constant;
            options.Retry.Delay = TimeSpan.Zero;
            options.Retry.MaxRetryAttempts = 3;
        });

        var serviceProvider = services.BuildServiceProvider();
        var clientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
        var httpClient = clientFactory.CreateClient("ResilientHcmClient");

        var options = new RestHcmConnectorOptions
        {
            Endpoint = "/api/v1/employees"
        };

        var connector = new GenericRestHcmConnector(httpClient, new DefaultHcmPayloadMapper(), options);

        // Act
        var result = await connector.GetEmployeesPageAsync(1, 10);

        // Assert
        result.Should().NotBeNull();
        result.Items.Should().HaveCount(2);
        requestCount.Should().Be(2, "O primeiro request retornou 503 e o pipeline de resiliência executou o retry com sucesso.");
    }

    [Theory]
    [InlineData("ACTIVE", EmployeeStatus.Active)]
    [InlineData("active", EmployeeStatus.Active)]
    [InlineData("Active", EmployeeStatus.Active)]
    [InlineData("inactive", EmployeeStatus.Inactive)]
    [InlineData("Inactive", EmployeeStatus.Inactive)]
    [InlineData("Terminated", EmployeeStatus.Inactive)]
    [InlineData("", EmployeeStatus.Inactive)]
    public async Task DefaultHcmPayloadMapper_MapsStatusStringsCorrectly(string statusString, EmployeeStatus expectedStatus)
    {
        // Arrange
        var json = $$"""
        {
          "items": [
            {
              "id": "EMP-001",
              "fullName": "Teste",
              "status": "{{statusString}}",
              "department": "Dept",
              "jobTitle": "Role",
              "extendedAttributes": {}
            }
          ],
          "pageNumber": 1,
          "pageSize": 1,
          "totalCount": 1
        }
        """;

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var mapper = new DefaultHcmPayloadMapper();

        // Act
        var result = await mapper.MapResponseAsync(response, 1, 1);

        // Assert
        result.Items.Should().ContainSingle();
        result.Items[0].Status.Should().Be(expectedStatus);
    }

    [Fact]
    public async Task DefaultHcmPayloadMapper_WhenHttpErrorReturned_ThrowsHttpRequestException()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("Internal server error")
        };

        var mapper = new DefaultHcmPayloadMapper();

        // Act
        var act = async () => await mapper.MapResponseAsync(response, 1, 10);

        // Assert
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public void DefaultHcmPayloadMapper_BuildPageUri_HandlesExistingQueryParams()
    {
        // Arrange
        var mapper = new DefaultHcmPayloadMapper();

        // Act
        var uriWithExistingQuery = mapper.BuildPageUri("/api/v1/employees?filter=all", 2, 50);
        var uriWithoutExistingQuery = mapper.BuildPageUri("/api/v1/employees", 1, 20);

        // Assert
        uriWithExistingQuery.Should().Be("/api/v1/employees?filter=all&pageNumber=2&pageSize=50");
        uriWithoutExistingQuery.Should().Be("/api/v1/employees?pageNumber=1&pageSize=20");
    }

    [Fact]
    public async Task GetEmployeesPageAsync_DisposesHttpResponseMessageAfterExecution()
    {
        // Arrange
        var trackingResponse = new TrackingHttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SampleStandardJson, Encoding.UTF8, "application/json")
        };

        var handler = new TestHttpMessageHandler((_, _) => Task.FromResult<HttpResponseMessage>(trackingResponse));
        var client = new HttpClient(handler);
        var mapper = new DefaultHcmPayloadMapper();
        var options = new RestHcmConnectorOptions
        {
            BaseUrl = "https://hcm.example.com",
            Endpoint = "/employees"
        };
        var connector = new GenericRestHcmConnector(client, mapper, options);

        // Act
        var result = await connector.GetEmployeesPageAsync(1, 10);

        // Assert
        result.Items.Should().NotBeEmpty();
        trackingResponse.WasDisposed.Should().BeTrue();
    }

    private sealed class TrackingHttpResponseMessage : HttpResponseMessage
    {
        public bool WasDisposed { get; private set; }

        public TrackingHttpResponseMessage(HttpStatusCode statusCode) : base(statusCode) { }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            WasDisposed = true;
        }
    }

    private sealed class TestHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public TestHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }
    }

    private sealed class CustomErpPayloadMapper : IHcmPayloadMapper
    {
        public string BuildPageUri(string endpoint, int pageNumber, int pageSize)
        {
            return $"{endpoint}/export?offset={pageNumber}&count={pageSize}";
        }

        public async Task<PagedResult<Employee>> MapResponseAsync(
            HttpResponseMessage response,
            int pageNumber,
            int pageSize,
            CancellationToken ct = default)
        {
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var items = new List<Employee>();
            foreach (var elem in root.GetProperty("colaboradores").EnumerateArray())
            {
                var id = EmployeeId.Create(elem.GetProperty("matricula").GetString()!).Value;
                var nome = elem.GetProperty("nome").GetString()!;
                var ativo = elem.GetProperty("ativo").GetBoolean();
                var status = ativo ? EmployeeStatus.Active : EmployeeStatus.Inactive;
                var area = elem.GetProperty("area").GetString()!;
                var posicao = elem.GetProperty("posicao").GetString()!;

                var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (elem.TryGetProperty("detalhes", out var detalhes))
                {
                    foreach (var prop in detalhes.EnumerateObject())
                    {
                        attrs[prop.Name] = prop.Value.GetString() ?? "";
                    }
                }

                items.Add(new Employee(id, nome, status, area, posicao, attrs));
            }

            var paginaAtual = root.GetProperty("indicePagina").GetInt32();
            var limite = root.GetProperty("limite").GetInt32();
            var contagemTotal = root.GetProperty("contagemTotal").GetInt32();
            var hasNext = (paginaAtual * limite) < contagemTotal;

            return new PagedResult<Employee>(items, paginaAtual, limite, contagemTotal, hasNext);
        }
    }
}
