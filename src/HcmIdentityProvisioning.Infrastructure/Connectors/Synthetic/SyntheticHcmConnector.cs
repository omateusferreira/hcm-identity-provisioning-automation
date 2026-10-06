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
        var resolvedPath = ResolvePath(path);
        if (!File.Exists(resolvedPath))
            throw new FileNotFoundException($"Fixtures file '{path}' not found.");

        var json = File.ReadAllText(resolvedPath);
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
            if (elem.TryGetProperty("extendedAttributes", out var attrsElem) && attrsElem.ValueKind == JsonValueKind.Object)
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
        var skip = Math.Max(0, (pageNumber - 1) * pageSize);
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

    private static string ResolvePath(string path)
    {
        if (File.Exists(path))
            return path;

        var baseCandidate = Path.Combine(AppContext.BaseDirectory, path);
        if (File.Exists(baseCandidate))
            return baseCandidate;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var testPath = Path.Combine(dir.FullName, path);
            if (File.Exists(testPath))
                return testPath;

            dir = dir.Parent;
        }

        return path;
    }
}
