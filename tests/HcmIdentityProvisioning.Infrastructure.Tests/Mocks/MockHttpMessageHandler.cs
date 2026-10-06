namespace HcmIdentityProvisioning.Infrastructure.Tests.Mocks;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;

public sealed class MockTokenCredential : TokenCredential
{
    private readonly string _token;

    public MockTokenCredential(string token = "mock-entra-bearer-token")
    {
        _token = token;
    }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        return new AccessToken(_token, DateTimeOffset.UtcNow.AddHours(2));
    }

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        return new ValueTask<AccessToken>(new AccessToken(_token, DateTimeOffset.UtcNow.AddHours(2)));
    }
}

public sealed record MockBatchSubResponse(
    string Id,
    int Status = 200,
    object? Body = null,
    IDictionary<string, string>? Headers = null);

public sealed class MockRoute
{
    private readonly Func<HttpRequestMessage, bool> _predicate;
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responseQueue = new();
    private Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? _defaultHandler;
    private readonly object _lock = new();

    public MockRoute(Func<HttpRequestMessage, bool> predicate)
    {
        _predicate = predicate;
    }

    public bool Matches(HttpRequestMessage request) => _predicate(request);

    public MockRoute RespondWith(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        lock (_lock)
        {
            _defaultHandler = (req, _) => Task.FromResult(handler(req));
        }
        return this;
    }

    public MockRoute RespondWithAsync(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        lock (_lock)
        {
            _defaultHandler = (req, _) => handler(req);
        }
        return this;
    }

    public MockRoute RespondWith(HttpResponseMessage response)
    {
        return RespondWith(_ => MockHttpMessageHandler.CloneResponse(response));
    }

    public MockRoute RespondWithJson(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        int? retryAfterSeconds = null,
        IDictionary<string, string>? headers = null)
    {
        return RespondWith(_ => MockHttpMessageHandler.CreateJsonResponse(json, statusCode, retryAfterSeconds, headers));
    }

    public MockRoute RespondWithStatus(
        HttpStatusCode statusCode,
        int? retryAfterSeconds = null,
        IDictionary<string, string>? headers = null)
    {
        return RespondWith(_ => MockHttpMessageHandler.CreateStatusResponse(statusCode, retryAfterSeconds, headers));
    }

    public MockRoute RespondSequence(params Func<HttpRequestMessage, HttpResponseMessage>[] handlers)
    {
        lock (_lock)
        {
            foreach (var h in handlers)
            {
                _responseQueue.Enqueue((req, _) => Task.FromResult(h(req)));
            }
        }
        return this;
    }

    public MockRoute RespondSequenceJson(params (HttpStatusCode statusCode, string json)[] responses)
    {
        lock (_lock)
        {
            foreach (var (statusCode, json) in responses)
            {
                _responseQueue.Enqueue((_, _) => Task.FromResult(
                    MockHttpMessageHandler.CreateJsonResponse(json, statusCode)));
            }
        }
        return this;
    }

    public MockRoute RespondSequenceJson(params (HttpStatusCode statusCode, string json, int? retryAfterSeconds)[] responses)
    {
        lock (_lock)
        {
            foreach (var (statusCode, json, retryAfter) in responses)
            {
                _responseQueue.Enqueue((_, _) => Task.FromResult(
                    MockHttpMessageHandler.CreateJsonResponse(json, statusCode, retryAfter)));
            }
        }
        return this;
    }

    internal async Task<HttpResponseMessage> ExecuteAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? handler = null;
        lock (_lock)
        {
            if (_responseQueue.Count > 0)
            {
                handler = _responseQueue.Dequeue();
            }
            else
            {
                handler = _defaultHandler;
            }
        }

        if (handler != null)
        {
            return await handler(request, cancellationToken);
        }

        return MockHttpMessageHandler.CreateJsonResponse(
            """{"error":{"code":"NoResponseConfigured","message":"Route matched but no response was configured."}}""",
            HttpStatusCode.InternalServerError);
    }
}

public sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly List<HttpRequestMessage> _sentRequests = new();
    private readonly List<string> _sentRequestBodies = new();
    private readonly List<MockRoute> _routes = new();
    private readonly object _lock = new();

    public IReadOnlyList<HttpRequestMessage> SentRequests
    {
        get
        {
            lock (_lock)
            {
                return _sentRequests.ToList().AsReadOnly();
            }
        }
    }

    public IReadOnlyList<string> SentRequestBodies
    {
        get
        {
            lock (_lock)
            {
                return _sentRequestBodies.ToList().AsReadOnly();
            }
        }
    }

    public HttpRequestMessage? LastRequest
    {
        get
        {
            lock (_lock)
            {
                return _sentRequests.LastOrDefault();
            }
        }
    }

    public string? LastRequestBody
    {
        get
        {
            lock (_lock)
            {
                return _sentRequestBodies.LastOrDefault();
            }
        }
    }

    public Func<HttpRequestMessage, HttpResponseMessage>? FallbackHandler { get; set; }
    public bool ThrowOnUnmatchedRequests { get; set; }

    public MockRoute When(Func<HttpRequestMessage, bool> predicate)
    {
        var route = new MockRoute(predicate);
        lock (_lock)
        {
            _routes.Add(route);
        }
        return route;
    }

    public MockRoute WhenUrlContains(string substring)
    {
        return When(req => req.RequestUri != null && req.RequestUri.ToString().Contains(substring, StringComparison.OrdinalIgnoreCase));
    }

    public MockRoute WhenMethodAndUrl(HttpMethod method, string substring)
    {
        return When(req => req.Method == method && req.RequestUri != null && req.RequestUri.ToString().Contains(substring, StringComparison.OrdinalIgnoreCase));
    }

    public MockRoute WhenGroups() => WhenUrlContains("/v1.0/groups");
    public MockRoute WhenUsers() => WhenUrlContains("/v1.0/users");
    public MockRoute WhenBatch() => WhenUrlContains("/v1.0/$batch");

    public MockHttpMessageHandler SetupRoute(Func<HttpRequestMessage, bool> predicate, Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        When(predicate).RespondWith(handler);
        return this;
    }

    public MockHttpMessageHandler SetupJson(
        string urlSubstring,
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        int? retryAfterSeconds = null,
        IDictionary<string, string>? headers = null)
    {
        WhenUrlContains(urlSubstring).RespondWithJson(json, statusCode, retryAfterSeconds, headers);
        return this;
    }

    public MockHttpMessageHandler SetupStatus(
        string urlSubstring,
        HttpStatusCode statusCode,
        int? retryAfterSeconds = null,
        IDictionary<string, string>? headers = null)
    {
        WhenUrlContains(urlSubstring).RespondWithStatus(statusCode, retryAfterSeconds, headers);
        return this;
    }

    public MockHttpMessageHandler SetupGroups(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
        => SetupJson("/groups", json, statusCode);

    public MockHttpMessageHandler SetupUsers(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
        => SetupJson("/users", json, statusCode);

    public MockHttpMessageHandler SetupBatch(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
        => SetupJson("/$batch", json, statusCode);

    public MockHttpMessageHandler SetupBatchThrottle(int retryAfterSeconds, string? errorJson = null)
    {
        var content = errorJson ?? """{"error":{"code":"activityLimitReached","message":"Client throttled by Graph API"}}""";
        return SetupJson("/$batch", content, HttpStatusCode.TooManyRequests, retryAfterSeconds);
    }

    public MockHttpMessageHandler SetupBatchThrottleThenOk(int retryAfterSeconds, string okJson)
    {
        WhenUrlContains("/$batch").RespondSequenceJson(
            (HttpStatusCode.TooManyRequests, """{"error":{"code":"activityLimitReached","message":"Client throttled by Graph API"}}""", retryAfterSeconds),
            (HttpStatusCode.OK, okJson, null)
        );
        return this;
    }

    public MockHttpMessageHandler SetupBatchResponses(params MockBatchSubResponse[] responses)
    {
        return SetupBatch(CreateBatchResponseBody(responses));
    }

    public HttpClient ToHttpClient(Uri? baseAddress = null)
    {
        var client = new HttpClient(this, disposeHandler: false);
        if (baseAddress != null)
        {
            client.BaseAddress = baseAddress;
        }
        return client;
    }

    public string GetSentRequestBody(int index)
    {
        lock (_lock)
        {
            return _sentRequestBodies[index];
        }
    }

    public HttpRequestMessage? FindRequest(string urlSubstring)
    {
        lock (_lock)
        {
            return _sentRequests.FirstOrDefault(r => r.RequestUri != null && r.RequestUri.ToString().Contains(urlSubstring, StringComparison.OrdinalIgnoreCase));
        }
    }

    public IReadOnlyList<HttpRequestMessage> FindRequests(string urlSubstring)
    {
        lock (_lock)
        {
            return _sentRequests.Where(r => r.RequestUri != null && r.RequestUri.ToString().Contains(urlSubstring, StringComparison.OrdinalIgnoreCase)).ToList().AsReadOnly();
        }
    }

    public void ClearRequests()
    {
        lock (_lock)
        {
            _sentRequests.Clear();
            _sentRequestBodies.Clear();
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _sentRequests.Clear();
            _sentRequestBodies.Clear();
            _routes.Clear();
            FallbackHandler = null;
            ThrowOnUnmatchedRequests = false;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = string.Empty;
        if (request.Content != null)
        {
            await request.Content.LoadIntoBufferAsync();
            body = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        lock (_lock)
        {
            _sentRequests.Add(request);
            _sentRequestBodies.Add(body);
        }

        MockRoute? matchedRoute = null;
        lock (_lock)
        {
            matchedRoute = _routes.FirstOrDefault(r => r.Matches(request));
        }

        if (matchedRoute != null)
        {
            return await matchedRoute.ExecuteAsync(request, cancellationToken);
        }

        if (FallbackHandler != null)
        {
            return FallbackHandler(request);
        }

        if (ThrowOnUnmatchedRequests)
        {
            throw new InvalidOperationException(
                $"MockHttpMessageHandler: No route matched request '{request.Method} {request.RequestUri}'.");
        }

        var errorJson = JsonSerializer.Serialize(new
        {
            error = new
            {
                code = "ResourceNotFound",
                message = $"No mock response configured for URI '{request.RequestUri}'."
            }
        });

        return CreateJsonResponse(errorJson, HttpStatusCode.NotFound);
    }

    public static HttpResponseMessage CreateJsonResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        int? retryAfterSeconds = null,
        IDictionary<string, string>? headers = null)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        if (retryAfterSeconds.HasValue)
        {
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfterSeconds.Value.ToString());
        }

        if (headers != null)
        {
            foreach (var (key, value) in headers)
            {
                response.Headers.TryAddWithoutValidation(key, value);
            }
        }

        return response;
    }

    public static HttpResponseMessage CreateStatusResponse(
        HttpStatusCode statusCode,
        int? retryAfterSeconds = null,
        IDictionary<string, string>? headers = null)
    {
        var response = new HttpResponseMessage(statusCode);

        if (retryAfterSeconds.HasValue)
        {
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfterSeconds.Value.ToString());
        }

        if (headers != null)
        {
            foreach (var (key, value) in headers)
            {
                response.Headers.TryAddWithoutValidation(key, value);
            }
        }

        return response;
    }

    public static HttpResponseMessage CloneResponse(HttpResponseMessage source)
    {
        var copy = new HttpResponseMessage(source.StatusCode);
        foreach (var header in source.Headers)
        {
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        if (source.Content != null)
        {
            var bytes = source.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            copy.Content = new ByteArrayContent(bytes);
            foreach (var header in source.Content.Headers)
            {
                copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
        return copy;
    }

    public static string CreateBatchResponseBody(IEnumerable<MockBatchSubResponse> responses)
    {
        var list = responses.Select(r =>
        {
            var dict = new Dictionary<string, object?>
            {
                ["id"] = r.Id,
                ["status"] = r.Status
            };

            if (r.Headers != null && r.Headers.Count > 0)
            {
                dict["headers"] = r.Headers;
            }

            if (r.Body != null)
            {
                if (r.Body is string jsonStr && (jsonStr.TrimStart().StartsWith('{') || jsonStr.TrimStart().StartsWith('[')))
                {
                    try
                    {
                        dict["body"] = JsonDocument.Parse(jsonStr).RootElement.Clone();
                    }
                    catch
                    {
                        dict["body"] = jsonStr;
                    }
                }
                else
                {
                    dict["body"] = r.Body;
                }
            }

            return dict;
        });

        return JsonSerializer.Serialize(new { responses = list });
    }

    public static string CreateBatchResponseBody(params MockBatchSubResponse[] responses)
        => CreateBatchResponseBody((IEnumerable<MockBatchSubResponse>)responses);

    public static string CreateODataCollectionResponse<T>(IEnumerable<T> items)
        => JsonSerializer.Serialize(new { value = items });
}
