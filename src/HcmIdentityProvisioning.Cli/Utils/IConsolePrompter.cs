namespace HcmIdentityProvisioning.Cli.Utils;

public interface IConsolePrompter
{
    bool IsInputRedirected { get; }
    bool Confirm(string message);
    string ReadMaskedPassword(string prompt);
}

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
