using RipperWorks.Core;

namespace RipperWorks.Organizer;

public enum FomodGroupType
{
    SelectAny,
    SelectExactlyOne,
    SelectAtMostOne,
    SelectAtLeastOne,
    SelectAll
}

public enum FomodPluginType
{
    Optional,
    Required,
    Recommended,
    NotUsable,
    CouldBeUsable
}

public sealed record FomodFileInstall(
    string Source,
    string Destination,
    int Priority = 0,
    bool IsFolder = false);

public sealed record FomodConditionFlag(
    string Name,
    string Value);

public sealed record FomodFlagDependency(
    string Name,
    string Value);

public sealed record FomodPlugin(
    string Id,
    string Name,
    string Description,
    string? ImagePath,
    FomodPluginType Type,
    IReadOnlyList<FomodFileInstall> Files,
    IReadOnlyList<FomodConditionFlag> ConditionFlags);

public sealed record FomodGroup(
    string Name,
    FomodGroupType Type,
    IReadOnlyList<FomodPlugin> Plugins);

public sealed record FomodStep(
    string Name,
    IReadOnlyList<FomodGroup> Groups);

public sealed record FomodConditionalInstall(
    IReadOnlyList<FomodFlagDependency> FlagDependencies,
    IReadOnlyList<FomodFileInstall> Files);

public sealed record FomodDefinition(
    string ModuleName,
    string? ModuleImage,
    IReadOnlyList<FomodFileInstall> RequiredFiles,
    IReadOnlyList<FomodStep> Steps,
    IReadOnlyList<FomodConditionalInstall> ConditionalInstalls,
    string? UnsupportedReason = null)
{
    public bool IsSupported => string.IsNullOrWhiteSpace(UnsupportedReason);

    public static FomodDefinition Unsupported(string reason) =>
        new(string.Empty, null, [], [], [], reason);
}
