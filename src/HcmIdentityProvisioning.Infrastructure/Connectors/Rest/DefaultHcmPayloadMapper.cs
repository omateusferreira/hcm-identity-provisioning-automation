using System.Text.Json;
using System.Text.Json.Serialization;
using HcmIdentityProvisioning.Domain.Common;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Enums;
using HcmIdentityProvisioning.Domain.ValueObjects;

namespace HcmIdentityProvisioning.Infrastructure.Connectors.Rest;

public sealed class DefaultHcmPayloadMapper : IHcmPayloadMapper
{
    private static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public string BuildPageUri(string endpoint, int pageNumber, int pageSize)
    {
        var separator = endpoint.Contains('?') ? "&" : "?";
        return $"{endpoint}{separator}pageNumber={pageNumber}&pageSize={pageSize}";
    }

    public async Task<PagedResult<Employee>> MapResponseAsync(
        HttpResponseMessage response,
        int pageNumber,
        int pageSize,
        CancellationToken ct = default)
    {
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        var envelope = await JsonSerializer.DeserializeAsync<DefaultHcmResponseEnvelope>(
            stream,
            DefaultJsonOptions,
            ct);

        var employees = new List<Employee>();
        if (envelope?.Items != null)
        {
            foreach (var item in envelope.Items)
            {
                var idResult = EmployeeId.Create(item.Id);
                if (!idResult.IsSuccess)
                {
                    throw new InvalidOperationException($"Invalid employee ID '{item.Id}': {idResult.Error}");
                }

                var status = string.Equals(item.Status, "active", StringComparison.OrdinalIgnoreCase)
                    ? EmployeeStatus.Active
                    : EmployeeStatus.Inactive;

                var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (item.ExtendedAttributes != null)
                {
                    foreach (var (key, jsonElement) in item.ExtendedAttributes)
                    {
                        var stringValue = jsonElement.ValueKind switch
                        {
                            JsonValueKind.String => jsonElement.GetString() ?? string.Empty,
                            JsonValueKind.Null => string.Empty,
                            _ => jsonElement.GetRawText().Trim('"')
                        };
                        attributes[key] = stringValue;
                    }
                }

                employees.Add(new Employee(
                    idResult.Value,
                    item.FullName ?? string.Empty,
                    status,
                    item.Department ?? string.Empty,
                    item.JobTitle ?? string.Empty,
                    attributes
                ));
            }
        }

        var resolvedPageNumber = envelope?.PageNumber is > 0 ? envelope.PageNumber.Value : pageNumber;
        var resolvedPageSize = envelope?.PageSize is > 0 ? envelope.PageSize.Value : pageSize;
        var resolvedTotalCount = envelope?.TotalCount ?? employees.Count;
        var hasNextPage = (resolvedPageNumber * resolvedPageSize) < resolvedTotalCount;

        return new PagedResult<Employee>(
            employees,
            resolvedPageNumber,
            resolvedPageSize,
            resolvedTotalCount,
            hasNextPage);
    }

    private sealed record DefaultHcmResponseEnvelope(
        [property: JsonPropertyName("items")] List<DefaultHcmEmployeeItem>? Items,
        [property: JsonPropertyName("pageNumber")] int? PageNumber,
        [property: JsonPropertyName("pageSize")] int? PageSize,
        [property: JsonPropertyName("totalCount")] int? TotalCount
    );

    private sealed record DefaultHcmEmployeeItem(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("fullName")] string? FullName,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("department")] string? Department,
        [property: JsonPropertyName("jobTitle")] string? JobTitle,
        [property: JsonPropertyName("extendedAttributes")] Dictionary<string, JsonElement>? ExtendedAttributes
    );
}
