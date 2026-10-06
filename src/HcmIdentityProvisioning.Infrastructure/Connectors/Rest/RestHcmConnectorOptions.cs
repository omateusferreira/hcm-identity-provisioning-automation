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
