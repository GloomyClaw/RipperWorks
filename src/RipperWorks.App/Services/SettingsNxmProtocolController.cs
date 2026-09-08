using RipperWorks.Core;

namespace RipperWorks.App.Services;

public sealed class SettingsNxmProtocolController(
    INxmProtocolRegistration? nxmProtocol,
    LocalizationService localization,
    string executablePath)
{
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
            setStatus(exception.Message);
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
            setStatus(exception.Message);
        }
    }

    public string RegistrationStatus =>
        nxmProtocol?.IsRegistered(executablePath) == true
            ? localization.Get("NxmRegistered")
            : localization.Get("NxmNotRegistered");

    public bool CanRegister =>
        nxmProtocol is not null &&
        !string.IsNullOrWhiteSpace(executablePath);
    public bool CanUnregister => nxmProtocol is not null;
}
