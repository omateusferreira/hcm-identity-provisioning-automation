namespace HcmIdentityProvisioning.Admin.Utils;

public sealed class ConsolePrompter : IConsolePrompter
{
    public bool IsInputRedirected => Console.IsInputRedirected;

    public bool Confirm(string message)
    {
        if (IsInputRedirected)
        {
            return false;
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write($"{message} [y/N]: ");
        Console.ResetColor();

        var input = Console.ReadLine()?.Trim().ToLowerInvariant();
        return input is "y" or "yes" or "s" or "sim";
    }

    public Task<bool> ConfirmAsync(string prompt, bool defaultResponse = false)
    {
        if (IsInputRedirected)
        {
            return Task.FromResult(defaultResponse);
        }

        var suffix = defaultResponse ? "[Y/n]" : "[y/N]";
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write($"{prompt} {suffix}: ");
        Console.ResetColor();

        var input = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(input))
        {
            return Task.FromResult(defaultResponse);
        }

        var confirmed = input is "y" or "yes" or "s" or "sim";
        return Task.FromResult(confirmed);
    }

    public Task<string> PromptAsync(string prompt, string? defaultValue = null)
    {
        if (IsInputRedirected)
        {
            return Task.FromResult(Console.ReadLine()?.Trim() ?? defaultValue ?? string.Empty);
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        if (!string.IsNullOrWhiteSpace(defaultValue))
        {
            Console.Write($"{prompt} [{defaultValue}]: ");
        }
        else
        {
            Console.Write($"{prompt}: ");
        }
        Console.ResetColor();

        var input = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(input) && defaultValue != null)
        {
            return Task.FromResult(defaultValue);
        }

        return Task.FromResult(input ?? string.Empty);
    }

    public string ReadMaskedPassword(string prompt)
    {
        if (IsInputRedirected)
        {
            return Console.ReadLine()?.Trim() ?? string.Empty;
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write(prompt);
        Console.ResetColor();

        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Remove(sb.Length - 1, 1);
                    Console.Write("\b \b");
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                sb.Append(key.KeyChar);
                Console.Write("*");
            }
        }

        return sb.ToString();
    }
}
