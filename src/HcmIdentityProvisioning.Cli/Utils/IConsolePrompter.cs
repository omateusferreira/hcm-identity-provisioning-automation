namespace HcmIdentityProvisioning.Cli.Utils;

public interface IConsolePrompter
{
    bool IsInputRedirected { get; }
    bool Confirm(string message);
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
}
