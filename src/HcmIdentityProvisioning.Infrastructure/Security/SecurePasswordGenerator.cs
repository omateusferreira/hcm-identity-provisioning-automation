using System.Security.Cryptography;
using HcmIdentityProvisioning.Domain.Policies;

namespace HcmIdentityProvisioning.Infrastructure.Security;

public sealed class SecurePasswordGenerator : ISecurePasswordGenerator
{
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnopqrstuvwxyz";
    private const string Digits = "23456789";
    private const string Special = "!@#$%^&*()_+-=[]{}|;:,.<>?";
    private static readonly string All = Upper + Lower + Digits + Special;

    public string GeneratePassword(int length = 24)
    {
        if (length < 12) length = 12;

        var chars = new char[length];
        chars[0] = Upper[RandomNumberGenerator.GetInt32(Upper.Length)];
        chars[1] = Lower[RandomNumberGenerator.GetInt32(Lower.Length)];
        chars[2] = Digits[RandomNumberGenerator.GetInt32(Digits.Length)];
        chars[3] = Special[RandomNumberGenerator.GetInt32(Special.Length)];

        for (int i = 4; i < length; i++)
        {
            chars[i] = All[RandomNumberGenerator.GetInt32(All.Length)];
        }

        RandomNumberGenerator.Shuffle(chars.AsSpan());
        return new string(chars);
    }
}
