using RipperWorks.Core;

namespace RipperWorks.App.Services;

/// <summary>
/// Settings credential presentation operations. Never stores decrypted secrets.
/// Status text is sanitized so keys never appear in the UI route.
/// </summary>
public sealed class SettingsCredentialController(
    IProtectedCredentialStore? credentials,
    INexusApiClient? nexusApi,
    LocalizationService localization,
    INxmProtocolRegistration? nxmProtocol,
    string executablePath)
{
    public async Task InitializeStatusAsync(
        Action<string> setStatus)
    {
        if (credentials is null)
            return;
        var status = await credentials.GetStatusAsync(
            CredentialIdentity.NexusDefault);
        setStatus(status.Status switch
        {
            CredentialPresenceStatus.Present =>
                localization.Get("NexusKeyStored"),
            CredentialPresenceStatus.Unreadable =>
                string.Format(
                    localization.Get("NexusKeyInvalid"),
                    "stored"),
            CredentialPresenceStatus.LegacyMigrationFailed =>
                string.Format(
                    localization.Get("NexusKeyInvalid"),
                    "migration"),
            _ => localization.Get("NexusKeyNotConfigured")
        });
    }

    public async Task ValidateAsync(
        string typedKey,
        Action<string> setStatus)
    {
        if (credentials is null || nexusApi is null)
            return;
        try
        {
            var trimmed = typedKey.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                var user = await nexusApi.ValidateApiKeyAsync(trimmed);
                setStatus(string.Format(
                    localization.Get("NexusKeyValid"),
                    user.Name));
                return;
            }

            await credentials.UseAsync(
                CredentialIdentity.NexusDefault,
                async (key, token) =>
                {
                    var user = await nexusApi.ValidateApiKeyAsync(
                        key,
                        token);
                    setStatus(string.Format(
                        localization.Get("NexusKeyValid"),
                        user.Name));
                });
        }
        catch (Exception exception)
        {
            setStatus(string.Format(
                localization.Get("NexusKeyInvalid"),
                CredentialUiSanitizer.Sanitize(
                    exception.Message,
                    typedKey)));
        }
    }

    public async Task<bool> TrySaveTypedKeyAsync(
        string typedKey,
        Action clearTypedKey,
        Action<string> setStatus)
    {
        if (credentials is null || string.IsNullOrWhiteSpace(typedKey))
            return false;
        try
        {
            await credentials.SaveAsync(
                CredentialIdentity.NexusDefault,
                typedKey.Trim());
            clearTypedKey();
            setStatus(localization.Get("NexusKeyStored"));
            return true;
        }
        catch (Exception exception)
        {
            setStatus(string.Format(
                localization.Get("SettingsError"),
                CredentialUiSanitizer.Sanitize(
                    exception.Message,
                    typedKey)));
            return false;
        }
    }

    public void Register(
        Action<string> setStatus,
        Action notifyRegistration)
    {
        try
        {
            nxmProtocol?.Register(executablePath);
            setStatus(localization.Get("NxmRegistered"));
            notifyRegistration();
        }
        catch (Exception exception)
        {
            setStatus(CredentialUiSanitizer.Sanitize(exception.Message));
        }
    }

    public void Unregister(
        Action<string> setStatus,
        Action notifyRegistration)
    {
        try
        {
            nxmProtocol?.Unregister();
            setStatus(localization.Get("NxmNotRegistered"));
            notifyRegistration();
        }
        catch (Exception exception)
        {
            setStatus(CredentialUiSanitizer.Sanitize(exception.Message));
        }
    }

    public string RegistrationStatus =>
        nxmProtocol?.IsRegistered(executablePath) == true
            ? localization.Get("NxmRegistered")
            : localization.Get("NxmNotRegistered");

    public bool CanValidate =>
        credentials is not null && nexusApi is not null;
    public bool CanRegister =>
        nxmProtocol is not null &&
        !string.IsNullOrWhiteSpace(executablePath);
    public bool CanUnregister => nxmProtocol is not null;
}
