namespace HcmIdentityProvisioning.Admin.Utils;

public interface IConsolePrompter
{
    bool IsInputRedirected { get; }
    bool Confirm(string message);
    Task<bool> ConfirmAsync(string prompt, bool defaultResponse = false);
    Task<string> PromptAsync(string prompt, string? defaultValue = null);
    string ReadMaskedPassword(string prompt);
}
