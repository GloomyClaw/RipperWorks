using System;
using System.IO;
using System.Text.RegularExpressions;

namespace RipperWorks.App.ViewModels;

public static class DiagnosticsLocalPathPolicy
{
    private static readonly Regex LocalDrivePathRegex = new(
        @"^[a-zA-Z]:\\[^/:*?""<>|\r\n]*$",
        RegexOptions.Compiled);

    public static bool IsValidLocalWindowsPath(string? path, Func<string, DriveType>? driveTypeResolver = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.Contains("://", StringComparison.Ordinal) || path.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return false;
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return false;
        if (path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\??\", StringComparison.Ordinal)) return false;
        if (path.Contains('"', StringComparison.Ordinal)) return false;

        if (!LocalDrivePathRegex.IsMatch(path)) return false;

        var driveRoot = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(driveRoot)) return false;

        try
        {
            var driveType = driveTypeResolver != null
                ? driveTypeResolver(driveRoot)
                : new DriveInfo(driveRoot).DriveType;

            // Reject network drives, non-rooted, and unknown drive types
            if (driveType == DriveType.Network ||
                driveType == DriveType.NoRootDirectory ||
                driveType == DriveType.Unknown)
            {
                return false;
            }

            // Permit local storage types (Fixed, Removable, Ram, CdRom if local)
            return driveType == DriveType.Fixed ||
                   driveType == DriveType.Removable ||
                   driveType == DriveType.Ram ||
                   driveType == DriveType.CDRom;
        }
        catch
        {
            // Fail closed on any drive resolution failure
            return false;
        }
    }
}
