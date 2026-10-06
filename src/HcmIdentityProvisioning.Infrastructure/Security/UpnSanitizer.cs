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
