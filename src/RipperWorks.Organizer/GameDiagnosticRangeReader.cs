using System;
using System.IO;
using System.Text;

namespace RipperWorks.Organizer;

public static class GameDiagnosticRangeReader
{
    private static readonly UTF8Encoding Utf8Lenient = new(false, false);

    public static (bool Success, string? Text) ReadCandidateSlice(DiagnosticFileDelta delta)
    {
        if (delta.MutationKind is DiagnosticMutationKind.Unchanged or DiagnosticMutationKind.Deleted or DiagnosticMutationKind.Unavailable)
            return (true, string.Empty);

        if (delta.Length <= 0)
            return (true, string.Empty);

        try
        {
            if (!File.Exists(delta.FullPath))
                return (false, null);

            using var stream = new FileStream(
                delta.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            // Full captured byte interval [StartOffset, StartOffset + Length) MUST exist. No partial clamping.
            if (stream.Length < delta.StartOffset + delta.Length || stream.Length <= delta.StartOffset)
                return (false, null);

            stream.Seek(delta.StartOffset, SeekOrigin.Begin);
            var buffer = new byte[delta.Length];
            var totalRead = 0;
            var toRead = (int)delta.Length;
            while (totalRead < toRead)
            {
                var r = stream.Read(buffer, totalRead, toRead - totalRead);
                if (r == 0) break;
                totalRead += r;
            }

            if (totalRead < delta.Length)
                return (false, null);

            var text = Utf8Lenient.GetString(buffer, 0, totalRead);
            if (delta.StartOffset == 0)
                text = text.TrimStart('\uFEFF');

            return (true, text);
        }
        catch
        {
            return (false, null);
        }
    }
}
