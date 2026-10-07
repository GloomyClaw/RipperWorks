using RipperWorks.Downloader;

namespace RipperWorks.App.Services;

public enum NexusAdultContentPermission
{
    Unknown = 0,
    Restricted = 1,
    Allowed = 2
}

public enum NexusAdultContentAccess
{
    Normal = 0,
    AdultAllowed = 1,
    AdultRestricted = 2,
    ClassificationUnavailableRestricted = 3
}

public interface INexusAdultContentPermissionProvider
{
    NexusAdultContentPermission CurrentPermission { get; }
}

public sealed class UnknownNexusAdultContentPermissionProvider :
    INexusAdultContentPermissionProvider
{
    public static UnknownNexusAdultContentPermissionProvider Instance { get; } =
        new();

    private UnknownNexusAdultContentPermissionProvider()
    {
    }

    public NexusAdultContentPermission CurrentPermission =>
        NexusAdultContentPermission.Unknown;
}

public sealed class NexusAdultContentAccessPolicy
{
    private readonly INexusAdultContentPermissionProvider _permissionProvider;

    public NexusAdultContentAccessPolicy(
        INexusAdultContentPermissionProvider permissionProvider)
    {
        _permissionProvider = permissionProvider ??
            throw new ArgumentNullException(nameof(permissionProvider));
    }

    public NexusAdultContentAccess Evaluate(
        NexusModRequirementEdge edge,
        bool isForward)
    {
        ArgumentNullException.ThrowIfNull(edge);
        var relatedIdentity = isForward
            ? NexusGameIdentityBridge.GetEffectiveTargetIdentity(edge.Target)
            : edge.Source;
        if (relatedIdentity is null)
            return NexusAdultContentAccess.Normal;

        var classification = isForward
            ? edge.TargetAdultContent
            : edge.SourceAdultContent;
        return Evaluate(classification, _permissionProvider.CurrentPermission);
    }

    public static NexusAdultContentAccess Evaluate(
        NexusAdultContentClassification classification,
        NexusAdultContentPermission permission) => classification switch
        {
            NexusAdultContentClassification.NonAdult =>
                NexusAdultContentAccess.Normal,
            NexusAdultContentClassification.Adult
                when permission == NexusAdultContentPermission.Allowed =>
                NexusAdultContentAccess.AdultAllowed,
            NexusAdultContentClassification.Adult =>
                NexusAdultContentAccess.AdultRestricted,
            NexusAdultContentClassification.Unknown =>
                NexusAdultContentAccess.ClassificationUnavailableRestricted,
            _ => NexusAdultContentAccess.ClassificationUnavailableRestricted
        };
}

public static class NexusAdultContentAccessExtensions
{
    public static bool IsRestricted(this NexusAdultContentAccess access) =>
        access is NexusAdultContentAccess.AdultRestricted or
            NexusAdultContentAccess.ClassificationUnavailableRestricted;
}
