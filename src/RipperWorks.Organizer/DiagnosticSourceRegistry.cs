using System;
using System.Collections.Generic;

namespace RipperWorks.Organizer;

public enum DiagnosticSourceRootKind
{
    GameRoot,
    LocalAppData
}

public sealed record DiagnosticNexusSignature(string GameDomain, long ModId);

public sealed class DiagnosticSourceDefinition
{
    public required string SourceId { get; init; }
    public required string DisplayName { get; init; }
    public required DiagnosticSourceRootKind RootKind { get; init; }
    public required bool AlwaysActive { get; init; }
    public IReadOnlyList<string> SignaturePaths { get; init; } = [];
    public IReadOnlyList<DiagnosticNexusSignature> SignatureNexusIdentities { get; init; } = [];
    public required string RelativeDirectory { get; init; }
    public required IReadOnlyList<string> FilePatterns { get; init; }
    public bool IsDirectorySource { get; init; }

    public bool IsActive(InstalledFrameworkState state)
    {
        if (AlwaysActive)
            return true;

        if (state is null)
            return false;

        foreach (var sig in SignatureNexusIdentities)
        {
            foreach (var inst in state.InstalledNexusIdentities)
            {
                if (inst.Matches(sig.GameDomain, sig.ModId))
                    return true;
            }
        }

        foreach (var signaturePath in SignaturePaths)
        {
            var normalized = signaturePath.Replace('\\', '/').TrimStart('/');
            if (state.InstalledRelativeGamePaths.Contains(normalized))
                return true;
        }

        return false;
    }
}

public static class DiagnosticSourceRegistry
{
    public static readonly IReadOnlyList<DiagnosticSourceDefinition> SupportedSources =
    [
        new DiagnosticSourceDefinition
        {
            SourceId = "REDengine",
            DisplayName = "Cyberpunk 2077 / REDengine",
            RootKind = DiagnosticSourceRootKind.LocalAppData,
            AlwaysActive = true,
            RelativeDirectory = "REDEngine/ReportQueue",
            FilePatterns = ["*"],
            IsDirectorySource = true
        },
        new DiagnosticSourceDefinition
        {
            SourceId = "RED4ext",
            DisplayName = "RED4ext",
            RootKind = DiagnosticSourceRootKind.GameRoot,
            AlwaysActive = false,
            SignaturePaths =
            [
                "red4ext/bin/x64/red4ext.dll",
                "red4ext/bin/red4ext.dll",
                "red4ext/red4ext.dll",
                "bin/x64/red4ext.dll",
                "bin/x64/red4ext.asi"
            ],
            SignatureNexusIdentities = [new("cyberpunk2077", 2380)],
            RelativeDirectory = "red4ext/logs",
            FilePatterns = ["red4ext.log", "red4ext-*.log"]
        },
        new DiagnosticSourceDefinition
        {
            SourceId = "redscript",
            DisplayName = "redscript",
            RootKind = DiagnosticSourceRootKind.GameRoot,
            AlwaysActive = false,
            SignaturePaths =
            [
                "engine/tools/scc.exe"
            ],
            SignatureNexusIdentities = [new("cyberpunk2077", 1511)],
            RelativeDirectory = "r6/logs",
            FilePatterns = ["redscript_rCURRENT.log"]
        },
        new DiagnosticSourceDefinition
        {
            SourceId = "CyberEngineTweaks",
            DisplayName = "Cyber Engine Tweaks",
            RootKind = DiagnosticSourceRootKind.GameRoot,
            AlwaysActive = false,
            SignaturePaths =
            [
                "bin/x64/plugins/cyber_engine_tweaks.asi",
                "bin/x64/plugins/cyber_engine_tweaks/cyber_engine_tweaks.dll"
            ],
            SignatureNexusIdentities = [new("cyberpunk2077", 107)],
            RelativeDirectory = "bin/x64/plugins/cyber_engine_tweaks",
            FilePatterns = ["cyber_engine_tweaks.log", "scripting.log"]
        },
        new DiagnosticSourceDefinition
        {
            SourceId = "ArchiveXL",
            DisplayName = "ArchiveXL",
            RootKind = DiagnosticSourceRootKind.GameRoot,
            AlwaysActive = false,
            SignaturePaths =
            [
                "red4ext/plugins/ArchiveXL/ArchiveXL.dll"
            ],
            SignatureNexusIdentities = [new("cyberpunk2077", 4198)],
            RelativeDirectory = "red4ext/plugins/ArchiveXL",
            FilePatterns = ["ArchiveXL*.log"]
        },
        new DiagnosticSourceDefinition
        {
            SourceId = "TweakXL",
            DisplayName = "TweakXL",
            RootKind = DiagnosticSourceRootKind.GameRoot,
            AlwaysActive = false,
            SignaturePaths =
            [
                "red4ext/plugins/TweakXL/TweakXL.dll"
            ],
            SignatureNexusIdentities = [new("cyberpunk2077", 4197)],
            RelativeDirectory = "red4ext/plugins/TweakXL",
            FilePatterns = ["TweakXL*.log"]
        },
        new DiagnosticSourceDefinition
        {
            SourceId = "Codeware",
            DisplayName = "Codeware",
            RootKind = DiagnosticSourceRootKind.GameRoot,
            AlwaysActive = false,
            SignaturePaths =
            [
                "red4ext/plugins/Codeware/Codeware.dll"
            ],
            SignatureNexusIdentities = [new("cyberpunk2077", 7780)],
            RelativeDirectory = "red4ext/plugins/Codeware",
            FilePatterns = ["Codeware*.log"]
        },
        new DiagnosticSourceDefinition
        {
            SourceId = "RedFileSystem",
            DisplayName = "RedFileSystem",
            RootKind = DiagnosticSourceRootKind.GameRoot,
            AlwaysActive = false,
            SignaturePaths =
            [
                "red4ext/plugins/RedFileSystem/RedFileSystem.dll",
                "red4ext/plugins/REDFileSystem/REDFileSystem.dll"
            ],
            SignatureNexusIdentities = [new("cyberpunk2077", 13378)],
            RelativeDirectory = "red4ext/logs",
            FilePatterns = ["redfilesystem.log", "redfilesystem-*.log"]
        }
    ];
}
