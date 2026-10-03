using System.Diagnostics;
using System.Globalization;
using System.Text;
using XREngine.Rendering;

namespace XREngine.Runtime.Text.FreeType;

/// <summary>Runs the optional desktop atlas tool for authored MSDF and MTSDF fonts.</summary>
public sealed class MsdfAtlasGenFontAtlasGenerator : IFontDistanceFieldAtlasGenerator
{
    public FontDistanceFieldAtlasResult Generate(in FontDistanceFieldAtlasRequest request)
    {
        if (!TryResolveExecutable(out string? executablePath))
            return new(false, -1, string.Empty, string.Empty,
                "msdf-atlas-gen.exe was not found. Run Tools/Dependencies/Get-MsdfAtlasGen.ps1 to install it.");

        string? atlasDirectory = Path.GetDirectoryName(request.AtlasPath);
        if (!string.IsNullOrWhiteSpace(atlasDirectory))
            Directory.CreateDirectory(atlasDirectory);

        string? metadataDirectory = Path.GetDirectoryName(request.MetadataPath);
        if (!string.IsNullOrWhiteSpace(metadataDirectory))
            Directory.CreateDirectory(metadataDirectory);

        string charsetPath = Path.Combine(
            atlasDirectory ?? Path.GetTempPath(),
            $"{Path.GetFileNameWithoutExtension(request.FontPath)}.{Guid.NewGuid():N}.msdf-charset.txt");
        try
        {
            WriteCharsetFile(charsetPath, request.CharacterSet);
            ProcessStartInfo startInfo = CreateStartInfo(executablePath, charsetPath, in request);
            using Process process = new() { StartInfo = startInfo };
            if (!process.Start())
                return new(false, -1, string.Empty, string.Empty, "msdf-atlas-gen.exe did not start.");

            // Both redirected pipes must drain while the child runs, or a full pipe can block exit.
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
            Task<string> standardError = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WhenAll(standardOutput, standardError).GetAwaiter().GetResult();
            return new(
                process.ExitCode == 0,
                process.ExitCode,
                standardOutput.Result,
                standardError.Result,
                null);
        }
        catch (Exception ex)
        {
            return new(false, -1, string.Empty, string.Empty, $"msdf-atlas-gen.exe could not generate the atlas: {ex.Message}");
        }
        finally
        {
            try
            {
                File.Delete(charsetPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static ProcessStartInfo CreateStartInfo(
        string executablePath,
        string charsetPath,
        in FontDistanceFieldAtlasRequest request)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
        };

        startInfo.ArgumentList.Add("-font");
        startInfo.ArgumentList.Add(request.FontPath);
        startInfo.ArgumentList.Add("-charset");
        startInfo.ArgumentList.Add(charsetPath);
        startInfo.ArgumentList.Add("-type");
        startInfo.ArgumentList.Add(request.AtlasType switch
        {
            EFontAtlasType.Msdf => "msdf",
            EFontAtlasType.Mtsdf => "mtsdf",
            _ => throw new ArgumentOutOfRangeException(nameof(request), "A distance-field atlas type is required."),
        });
        startInfo.ArgumentList.Add("-format");
        startInfo.ArgumentList.Add("png");
        startInfo.ArgumentList.Add("-imageout");
        startInfo.ArgumentList.Add(request.AtlasPath);
        startInfo.ArgumentList.Add("-json");
        startInfo.ArgumentList.Add(request.MetadataPath);
        startInfo.ArgumentList.Add("-size");
        startInfo.ArgumentList.Add(request.FontSize.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-pxrange");
        startInfo.ArgumentList.Add(request.PixelRange.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-pxpadding");
        startInfo.ArgumentList.Add(request.InnerPixelPadding.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-outerpxpadding");
        startInfo.ArgumentList.Add(request.OuterPixelPadding.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-coloringstrategy");
        startInfo.ArgumentList.Add("inktrap");
        startInfo.ArgumentList.Add("-pxalign");
        startInfo.ArgumentList.Add("on");
        startInfo.ArgumentList.Add("-scanline");
        startInfo.ArgumentList.Add("-yorigin");
        startInfo.ArgumentList.Add("top");
        startInfo.ArgumentList.Add("-threads");
        startInfo.ArgumentList.Add(request.ThreadCount.ToString(CultureInfo.InvariantCulture));
        return startInfo;
    }

    private static void WriteCharsetFile(string path, IReadOnlyCollection<uint> characterSet)
    {
        // Codepoints are required because glyph indices cannot be mapped by the metadata parser.
        using StreamWriter writer = new(path, append: false, Encoding.ASCII);
        foreach (uint codepoint in characterSet)
        {
            if (codepoint >= 0x20 && codepoint <= 0x10FFFF)
                writer.WriteLine($"0x{codepoint:X4}");
        }
    }

    private static bool TryResolveExecutable(
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? executablePath)
    {
        executablePath = null;
        string[] candidateRelativePaths =
        [
            Path.Combine("Build", "Dependencies", "MsdfAtlasGen", "msdf-atlas-gen.exe"),
            "msdf-atlas-gen.exe",
        ];

        string? basePath = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(basePath))
        {
            foreach (string relativePath in candidateRelativePaths)
            {
                string candidate = Path.Combine(basePath, relativePath);
                if (File.Exists(candidate))
                {
                    executablePath = candidate;
                    return true;
                }
            }

            basePath = Path.GetDirectoryName(basePath);
        }

        return false;
    }
}
