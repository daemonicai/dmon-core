using Microsoft.AspNetCore.DataProtection;

namespace Dmail.Services;

public sealed class TokenProtectionService
{
    private readonly IDataProtector _protector;

    public TokenProtectionService(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("dmail.tokens");
    }

    public string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return plaintext;
        return _protector.Protect(plaintext);
    }

    public string? Unprotect(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext))
            return ciphertext;

        try
        {
            return _protector.Unprotect(ciphertext);
        }
        catch (Exception)
        {
            // Decryption failure — key missing, corrupted, or rotated away
            return null;
        }
    }
}
