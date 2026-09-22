using System.Security.Cryptography;
using System.Text;
using DictationApp.Core.Abstractions;

namespace DictationApp.Windows.Security;

/// <summary>
/// DPAPI (CurrentUser scope) so the API key in settings.json is unreadable by other users and by
/// anyone who copies the file. The optional entropy binds the blob to this app.
/// </summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DictationApp.v1");

    public string Protect(string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var blob = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(blob);
    }

    public string? Unprotect(string protectedValue)
    {
        try
        {
            var blob = Convert.FromBase64String(protectedValue);
            var bytes = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
