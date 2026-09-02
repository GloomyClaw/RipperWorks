using System.Security.Cryptography;
using System.Text;

namespace RipperWorks.Infrastructure;

/// <summary>
/// Current-path DPAPI blob protect/unprotect and atomic file operations.
/// One responsibility: ciphertext blob I/O for a single path.
/// </summary>
internal static class DpapiCredentialBlobOperations
{
    internal static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("ModDownloader.Nexus.ApiKey.v1");

    internal static async Task WriteAtomicAsync(
        string path,
        string secret,
        CancellationToken cancellationToken,
        Func<string, CancellationToken, Task>? phaseHook = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = GetTemporaryPath(path);
        var protectedValue = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(secret),
            Entropy,
            DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(
            temporaryPath,
            protectedValue,
            cancellationToken).ConfigureAwait(false);
        if (phaseHook is not null)
        {
            await phaseHook(
                    CredentialMigrationPhases.AfterCurrentTempWrite,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temporaryPath, path, overwrite: true);
        if (phaseHook is not null)
        {
            await phaseHook(
                    CredentialMigrationPhases.AfterAtomicReplace,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    internal static bool TryDecrypt(string path, out string secret)
    {
        secret = string.Empty;
        try
        {
            var protectedValue = File.ReadAllBytes(path);
            secret = Encoding.UTF8.GetString(
                ProtectedData.Unprotect(
                    protectedValue,
                    Entropy,
                    DataProtectionScope.CurrentUser));
            return !string.IsNullOrWhiteSpace(secret);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static string? HashFile(string path)
    {
        if (!File.Exists(path))
            return null;
        var bytes = File.ReadAllBytes(path);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    internal static string GetTemporaryPath(string path) => path + ".tmp";

    internal static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
