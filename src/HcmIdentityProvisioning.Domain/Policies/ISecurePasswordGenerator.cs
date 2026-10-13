namespace HcmIdentityProvisioning.Domain.Policies;

public interface ISecurePasswordGenerator
{
    string GeneratePassword(int length = 24);
}
