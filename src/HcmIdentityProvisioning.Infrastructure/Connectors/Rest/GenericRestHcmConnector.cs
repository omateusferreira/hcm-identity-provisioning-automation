using System.Net.Http.Headers;
using HcmIdentityProvisioning.Domain.Common;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Ports;
using Microsoft.Extensions.Options;

namespace HcmIdentityProvisioning.Infrastructure.Connectors.Rest;

public sealed class GenericRestHcmConnector : IHcmConnector
{
    private readonly HttpClient _httpClient;
    private readonly IHcmPayloadMapper _mapper;
    private readonly RestHcmConnectorOptions _options;

    public GenericRestHcmConnector(
        HttpClient httpClient,
        IHcmPayloadMapper mapper,
        IOptions<RestHcmConnectorOptions> options)
        : this(httpClient, mapper, options?.Value ?? throw new ArgumentNullException(nameof(options)))
    {
    }

    public GenericRestHcmConnector(
        HttpClient httpClient,
        IHcmPayloadMapper mapper,
        RestHcmConnectorOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<PagedResult<Employee>> GetEmployeesPageAsync(
        int pageNumber,
        int pageSize,
        CancellationToken ct = default)
    {
        var relativeOrAbsoluteUri = _mapper.BuildPageUri(_options.Endpoint, pageNumber, pageSize);
        var requestUri = ResolveRequestUri(relativeOrAbsoluteUri);

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);

        ApplyAuthenticationAndHeaders(request);

        var response = await _httpClient.SendAsync(request, ct);

        return await _mapper.MapResponseAsync(response, pageNumber, pageSize, ct);
    }

    private Uri ResolveRequestUri(string pageUri)
    {
        if (Uri.TryCreate(pageUri, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri;
        }

        if (_httpClient.BaseAddress != null)
        {
            return new Uri(pageUri, UriKind.RelativeOrAbsolute);
        }

        if (!string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            var normalizedBase = _options.BaseUrl.EndsWith('/') ? _options.BaseUrl : _options.BaseUrl + "/";
            var baseUri = new Uri(normalizedBase, UriKind.Absolute);
            var relative = pageUri.TrimStart('/');
            return new Uri(baseUri, relative);
        }

        return new Uri(pageUri, UriKind.RelativeOrAbsolute);
    }

    private void ApplyAuthenticationAndHeaders(HttpRequestMessage request)
    {
        switch (_options.AuthScheme)
        {
            case HcmAuthScheme.Bearer when !string.IsNullOrWhiteSpace(_options.BearerToken):
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.BearerToken);
                break;

            case HcmAuthScheme.ApiKey when !string.IsNullOrWhiteSpace(_options.ApiKey):
                var headerName = string.IsNullOrWhiteSpace(_options.ApiKeyHeaderName)
                    ? "X-Api-Key"
                    : _options.ApiKeyHeaderName;
                request.Headers.TryAddWithoutValidation(headerName, _options.ApiKey);
                break;

            case HcmAuthScheme.Basic:
                var basicToken = !string.IsNullOrWhiteSpace(_options.BearerToken)
                    ? _options.BearerToken
                    : _options.ApiKey;
                if (!string.IsNullOrWhiteSpace(basicToken))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicToken);
                }
                break;

            case HcmAuthScheme.None:
            default:
                break;
        }

        if (_options.CustomHeaders != null)
        {
            foreach (var (key, value) in _options.CustomHeaders)
            {
                request.Headers.TryAddWithoutValidation(key, value);
            }
        }
    }
}
