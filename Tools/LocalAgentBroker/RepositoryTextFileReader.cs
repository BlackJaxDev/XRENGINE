using System.Security.Cryptography;
using System.Text;
using XREngine.AgentOrchestration;

namespace XREngine.LocalAgentBroker;

/// <summary>
/// Reads and hashes one bounded UTF-8 repository file.
/// </summary>
internal sealed class RepositoryTextFileReader(RepositoryPathPolicy pathPolicy)
{
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly RepositoryPathPolicy _pathPolicy =
        pathPolicy ?? throw new ArgumentNullException(nameof(pathPolicy));

    public AgentContextFileSnapshot Read(
        string fullPath,
        string relativePath,
        int maxRawBytes,
        int? startLine = null,
        int? endLine = null,
        string? expectedSha256 = null)
    {
        if (maxRawBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxRawBytes));

        byte[] rawBytes;
        long beforeLength;
        DateTime beforeWriteUtc;
        try
        {
            var before = new FileInfo(fullPath);
            beforeLength = before.Length;
            beforeWriteUtc = before.LastWriteTimeUtc;
            if (beforeLength > maxRawBytes)
            {
                throw new ArgumentException(
                    $"Repository file '{relativePath}' exceeds the {maxRawBytes}-byte read limit.");
            }

            using FileStream stream = _pathPolicy.OpenReadValidated(fullPath);
            if (stream.Length > maxRawBytes)
            {
                throw new ArgumentException(
                    $"Repository file '{relativePath}' exceeds the {maxRawBytes}-byte read limit.");
            }
            rawBytes = new byte[checked((int)stream.Length)];
            int offset = 0;
            while (offset < rawBytes.Length)
            {
                int read = stream.Read(rawBytes, offset, rawBytes.Length - offset);
                if (read == 0)
                    break;
                offset += read;
            }
            if (offset != rawBytes.Length)
                throw new IOException("The repository file changed while it was being read.");

            var after = new FileInfo(fullPath);
            if (after.Length != beforeLength || after.LastWriteTimeUtc != beforeWriteUtc)
                throw new IOException("The repository file changed while it was being read.");
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or OverflowException)
        {
            throw new ArgumentException($"Repository file '{relativePath}' could not be read safely.");
        }

        string sha256 = Convert.ToHexString(SHA256.HashData(rawBytes)).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(expectedSha256)
            && !string.Equals(sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Repository file '{relativePath}' does not match expected_sha256.");
        }

        ReadOnlySpan<byte> textBytes = rawBytes;
        if (textBytes.Length >= 3
            && textBytes[0] == 0xEF
            && textBytes[1] == 0xBB
            && textBytes[2] == 0xBF)
        {
            textBytes = textBytes[3..];
        }

        string text;
        try
        {
            text = s_strictUtf8.GetString(textBytes);
        }
        catch (DecoderFallbackException)
        {
            throw new ArgumentException(
                $"Repository file '{relativePath}' is not strict UTF-8 text.");
        }
        if (text.Contains('\0'))
            throw new ArgumentException($"Repository file '{relativePath}' appears to be binary.");
        if (text.Any(static character => character < ' ' && character is not '\r' and not '\n' and not '\t'))
            throw new ArgumentException($"Repository file '{relativePath}' contains unsupported control characters.");
        if (text.Contains("-----BEGIN PRIVATE KEY-----", StringComparison.Ordinal)
            || text.Contains("-----BEGIN RSA PRIVATE KEY-----", StringComparison.Ordinal)
            || text.Contains("-----BEGIN OPENSSH PRIVATE KEY-----", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Repository file '{relativePath}' contains private-key material.");
        }

        string normalizedText = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        string[] lines = normalizedText.Length == 0 ? [] : normalizedText.Split('\n');
        if (lines.Length == 0)
        {
            if (startLine.HasValue || endLine.HasValue)
                throw new ArgumentException($"Repository file '{relativePath}' is empty.");
            return new AgentContextFileSnapshot
            {
                Path = relativePath,
                StartLine = 0,
                EndLine = 0,
                TotalLines = 0,
                RawByteLength = rawBytes.LongLength,
                Sha256 = sha256,
                Content = string.Empty,
            };
        }

        int selectedStart = startLine ?? 1;
        int selectedEnd = endLine ?? lines.Length;
        if (selectedStart > lines.Length)
            throw new ArgumentException($"Repository file '{relativePath}' has only {lines.Length} lines.");
        selectedEnd = Math.Min(selectedEnd, lines.Length);
        string content = string.Join('\n', lines[(selectedStart - 1)..selectedEnd]);
        return new AgentContextFileSnapshot
        {
            Path = relativePath,
            StartLine = selectedStart,
            EndLine = selectedEnd,
            TotalLines = lines.Length,
            RawByteLength = rawBytes.LongLength,
            Sha256 = sha256,
            Content = content,
        };
    }
}
