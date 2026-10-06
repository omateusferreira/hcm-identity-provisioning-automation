namespace HcmIdentityProvisioning.Infrastructure.Notifications;

public sealed class GraphEmailDeliveryOptions
{
    public string SenderEmail { get; set; } = string.Empty;
    public string Subject { get; set; } = "Suas credenciais de primeiro acesso - Bem-vindo(a)!";
    public bool SaveToSentItems { get; set; } = false;
}
