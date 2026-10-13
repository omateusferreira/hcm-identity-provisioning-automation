namespace HcmIdentityProvisioning.Admin.Utils;

/// <summary>
/// Provides secure file path resolution for configuration and data assets.
/// In Release builds, only explicit paths, environment variables, or application directory files are accepted.
/// Development tree search is strictly gated behind #if DEBUG.
/// </summary>
public static class PathResolver
{
    private const string RulesFileName = "rules.json";
    private const string FixturesFileName = "synthetic-employees.json";

    public static string ResolveRulesPath(string? customPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            var fullPath = Path.GetFullPath(customPath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"The specified rules file was not found: '{fullPath}'", fullPath);
            }
            return fullPath;
        }

        var envPath = Environment.GetEnvironmentVariable("RULES_FILE_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            var fullPath = Path.GetFullPath(envPath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"Environment variable RULES_FILE_PATH points to non-existent file: '{fullPath}'", fullPath);
            }
            return fullPath;
        }

        // 1. Application Base Directory: Rules/rules.json
        var appRulesSubdir = Path.Combine(AppContext.BaseDirectory, "Rules", RulesFileName);
        if (File.Exists(appRulesSubdir))
        {
            return appRulesSubdir;
        }

        // 2. Application Base Directory: rules.json
        var appRulesRoot = Path.Combine(AppContext.BaseDirectory, RulesFileName);
        if (File.Exists(appRulesRoot))
        {
            return appRulesRoot;
        }

#if DEBUG
        // Development fallback: only permitted during Debug builds to avoid CWE-426 in production
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidateSrc = Path.Combine(current.FullName, "src", "HcmIdentityProvisioning.Infrastructure", "Rules", RulesFileName);
            if (File.Exists(candidateSrc))
            {
                return candidateSrc;
            }

            var candidateLocal = Path.Combine(current.FullName, "Rules", RulesFileName);
            if (File.Exists(candidateLocal))
            {
                return candidateLocal;
            }

            current = current.Parent;
        }
#endif

        throw new FileNotFoundException(
            $"Could not find '{RulesFileName}'. Ensure it is located in '{Path.Combine(AppContext.BaseDirectory, "Rules")}' " +
            "or specify the path via the RULES_FILE_PATH environment variable or --rules CLI option.");
    }

    public static string ResolveFixturesPath(string? customPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            var fullPath = Path.GetFullPath(customPath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"The specified fixtures file was not found: '{fullPath}'", fullPath);
            }
            return fullPath;
        }

        var envPath = Environment.GetEnvironmentVariable("FIXTURES_FILE_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            var fullPath = Path.GetFullPath(envPath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"Environment variable FIXTURES_FILE_PATH points to non-existent file: '{fullPath}'", fullPath);
            }
            return fullPath;
        }

        // 1. Application Base Directory: fixtures/synthetic-employees.json
        var appFixturesSubdir = Path.Combine(AppContext.BaseDirectory, "fixtures", FixturesFileName);
        if (File.Exists(appFixturesSubdir))
        {
            return appFixturesSubdir;
        }

        // 2. Application Base Directory: synthetic-employees.json
        var appFixturesRoot = Path.Combine(AppContext.BaseDirectory, FixturesFileName);
        if (File.Exists(appFixturesRoot))
        {
            return appFixturesRoot;
        }

#if DEBUG
        // Development fallback: only permitted during Debug builds to avoid CWE-426 in production
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "fixtures", FixturesFileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }
#endif

        throw new FileNotFoundException(
            $"Could not find '{FixturesFileName}'. Ensure it is located in '{Path.Combine(AppContext.BaseDirectory, "fixtures")}' " +
            "or specify the path via the FIXTURES_FILE_PATH environment variable or --fixtures CLI option.");
    }
}
