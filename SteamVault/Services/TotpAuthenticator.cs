using SteamKit2.Authentication;

namespace SteamVault.Services;

/// <summary>
/// SteamKit2 IAuthenticator that generates codes from maFile shared_secret.
/// </summary>
public sealed class TotpAuthenticator : IAuthenticator
{
    private readonly string _sharedSecret;

    public TotpAuthenticator(string sharedSecret) => _sharedSecret = sharedSecret;

    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
    {
        return Task.FromResult(SteamTotp.GenerateAuthCode(_sharedSecret));
    }

    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
    {
        // maFile accounts use device 2FA, not email
        return Task.FromResult(SteamTotp.GenerateAuthCode(_sharedSecret));
    }

    public Task<bool> AcceptDeviceConfirmationAsync()
    {
        // Prefer providing code via GetDeviceCodeAsync
        return Task.FromResult(false);
    }
}
