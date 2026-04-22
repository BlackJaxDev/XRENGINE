using ImageMagick;
using MemoryPack;
using SharpFont;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SkiaSharp;
using System.Buffers;
using System.Numerics;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace XREngine.Rendering
{
    public enum EFontAtlasType
    {
        Bitmap,
        Msdf,
        Mtsdf,
    }

    [MemoryPackable]
    [XR3rdPartyExtensions(typeof(XREngine.Data.XRFontImportOptions), "otf", "ttf")]
    public partial class FontGlyphSet : XRAsset
    {
        static partial void StaticConstructor()
            => EnsureNativeFreetypeAvailable();

        [MemoryPackConstructor]
        public FontGlyphSet(List<string> characters, Dictionary<string, Glyph>? glyphs, XRTexture2D? atlas)
        {
            _characters = characters;
            _glyphs = glyphs;
            _atlas = atlas;
        }

        public FontGlyphSet() { }

        private const float DefaultBitmapFontDrawSize = 128.0f;
        private const float DefaultBitmapMipmapFontDrawSize = 256.0f;
        private const float DefaultWorldMtsdfFontSize = 72.0f;
        private const float DefaultWorldMtsdfPixelRange = 10.0f;
        private const float DefaultUiMtsdfFontSize = 96.0f;
        private const float DefaultUiMtsdfPixelRange = 12.0f;
        private const int BitmapAtlasPadding = 8;
        private const float DefaultMsdfDistanceRangeMiddle = 0.5f;
        private const string FontDiagnosticsLogName = "font-diagnostics.log";
        private static readonly ConcurrentDictionary<string, byte> ForcedReloadAttemptedPaths = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, byte> MissingAtlasRecoveryAttemptedKeys = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, Lazy<FontGlyphSet>> DirectEngineFontCache = new(StringComparer.OrdinalIgnoreCase);

        private List<string> _characters = [];
        public List<string> Characters
        {
            get => _characters;
            set => SetField(ref _characters, value);
        }

        private Dictionary<string, Glyph>? _glyphs;
        public Dictionary<string, Glyph>? Glyphs
        {
            get => _glyphs;
            set => SetField(ref _glyphs, value);
        }

        private XRTexture2D? _atlas;
        public XRTexture2D? Atlas
        {
            get => _atlas;
            set => SetField(ref _atlas, value);
        }

        private EFontAtlasType _atlasType;
        public EFontAtlasType AtlasType
        {
            get => _atlasType;
            set => SetField(ref _atlasType, value);
        }

        private float _distanceRange;
        public float DistanceRange
        {
            get => _distanceRange;
            set => SetField(ref _distanceRange, value);
        }

        private float _distanceRangeMiddle = DefaultMsdfDistanceRangeMiddle;
        public float DistanceRangeMiddle
        {
            get => _distanceRangeMiddle;
            set => SetField(ref _distanceRangeMiddle, value);
        }

        private float _layoutEmSize = DefaultBitmapFontDrawSize;
        public float LayoutEmSize
        {
            get => _layoutEmSize;
            set => SetField(ref _layoutEmSize, value);
        }

        public override void Reload(string path)
            => Load3rdParty(path);

        public override bool Load3rdParty(string filePath)
            => ImportFont(filePath, ResolveImportOptions(filePath), auxiliaryFileName => ResolveFallbackAuxiliaryPath(filePath, auxiliaryFileName));

        public override bool Load3rdParty(string filePath, AssetImportContext context)
            => ImportFont(filePath, ResolveImportOptions(filePath), context.ResolveAuxiliaryPath);

        public override bool Load3rdParty(string filePath, object? importOptions, AssetImportContext context)
            => ImportFont(filePath, importOptions as XRFontImportOptions ?? ResolveImportOptions(filePath), context.ResolveAuxiliaryPath);

        public override bool Import3rdParty(string filePath, object? importOptions)
            => ImportFont(filePath, importOptions as XRFontImportOptions ?? ResolveImportOptions(filePath), auxiliaryFileName => ResolveFallbackAuxiliaryPath(filePath, auxiliaryFileName));

        private static XRFontImportOptions ResolveImportOptions(string filePath)
        {
            try
            {
                return Engine.Assets.GetOrCreateThirdPartyImportOptions(filePath, typeof(FontGlyphSet)) as XRFontImportOptions ?? new XRFontImportOptions();
            }
            catch
            {
                return new XRFontImportOptions();
            }
        }

        private string? ResolveFallbackAuxiliaryPath(string sourceFilePath, string auxiliaryFileName)
        {
            string? primaryDirectory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrWhiteSpace(primaryDirectory))
                return Path.Combine(primaryDirectory, auxiliaryFileName);

            string? sourceDirectory = Path.GetDirectoryName(sourceFilePath);
            if (!string.IsNullOrWhiteSpace(sourceDirectory))
                return Path.Combine(sourceDirectory, auxiliaryFileName);

            return null;
        }

        private bool ImportFont(string filePath, XRFontImportOptions options, Func<string, string?> resolveAuxiliaryPath)
        {
            string name = Path.GetFileNameWithoutExtension(filePath);

            using Library lib = new();
            using Face face = new(lib, filePath);

            HashSet<uint> characterSet = GetSupportedCharacters(face);
            List<string> characters = [];
            foreach (uint codepoint in characterSet)
            {
                if (codepoint >= 0x20 && codepoint <= 0x10FFFF)
                    characters.Add(char.ConvertFromUtf32((int)codepoint));
            }

            Characters = characters;

            bool preferDistanceField = options.AtlasMode != EFontAtlasImportMode.Bitmap;
            if (preferDistanceField)
            {
                string fieldSuffix = GetDistanceFieldAuxiliarySuffix(options.AtlasMode);
                string? distanceFieldAtlasPath = resolveAuxiliaryPath($"{name}.{fieldSuffix}.png");
                string? distanceFieldMetadataPath = resolveAuxiliaryPath($"{name}.{fieldSuffix}.json");
                if (!string.IsNullOrWhiteSpace(distanceFieldAtlasPath) && !string.IsNullOrWhiteSpace(distanceFieldMetadataPath) &&
                    TryGenerateDistanceFieldAtlas(filePath, distanceFieldAtlasPath, distanceFieldMetadataPath, options, characterSet))
                {
                    Debug.WriteAuxiliaryLog(FontDiagnosticsLogName, $"Font import success: source='{filePath}', atlasType={AtlasType}, glyphs={Glyphs?.Count ?? 0}, atlas='{Atlas?.OriginalPath ?? Atlas?.FilePath ?? distanceFieldAtlasPath}', range={DistanceRange}, middle={DistanceRangeMiddle}, layoutEm={LayoutEmSize}");
                    Debug.LogWarning($"Font '{name}' loaded with {AtlasType} atlas ({Glyphs?.Count ?? 0} glyphs, range={DistanceRange}, middle={DistanceRangeMiddle}).");
                    return true;
                }

                Debug.WriteAuxiliaryLog(FontDiagnosticsLogName, $"Font import fallback: source='{filePath}', requested={options.AtlasMode}, atlasPathNull={string.IsNullOrWhiteSpace(distanceFieldAtlasPath)}, metadataPathNull={string.IsNullOrWhiteSpace(distanceFieldMetadataPath)}");
                Debug.LogWarning($"Font '{name}': {options.AtlasMode} generation failed, falling back to bitmap. (auxPath null? png={string.IsNullOrWhiteSpace(distanceFieldAtlasPath)}, json={string.IsNullOrWhiteSpace(distanceFieldMetadataPath)})");

                if ((options.AtlasMode == EFontAtlasImportMode.Msdf || options.AtlasMode == EFontAtlasImportMode.Mtsdf) && !options.AllowBitmapFallback)
                    return false;
            }

            using SKTypeface? typeface = SKTypeface.FromFile(filePath);
            if (typeface is null)
            {
                Debug.LogWarning($"Failed to load SKTypeface from '{filePath}'");
                return false;
            }

            string? atlasPath = resolveAuxiliaryPath($"{name}.png");
            if (string.IsNullOrWhiteSpace(atlasPath))
            {
                Debug.LogWarning($"Failed to resolve atlas output path for font '{filePath}'");
                return false;
            }

            GenerateBitmapFontAtlas(typeface, characters, atlasPath, options.BitmapFontDrawSize > 0.0f ? options.BitmapFontDrawSize : DefaultBitmapFontDrawSize);
            AtlasType = EFontAtlasType.Bitmap;
            DistanceRange = 0.0f;
            DistanceRangeMiddle = DefaultMsdfDistanceRangeMiddle;
            Debug.WriteAuxiliaryLog(FontDiagnosticsLogName, $"Font import success: source='{filePath}', atlasType={AtlasType}, glyphs={Glyphs?.Count ?? 0}, atlas='{Atlas?.OriginalPath ?? Atlas?.FilePath ?? atlasPath}', drawSize={options.BitmapFontDrawSize}, layoutEm={LayoutEmSize}");
            Debug.LogWarning($"Font '{name}' loaded with bitmap atlas ({Glyphs?.Count ?? 0} glyphs, drawSize={options.BitmapFontDrawSize}).");
            return true;
        }

        private static void EnsureNativeFreetypeAvailable()
        {
            try
            {
                string baseDir = AppContext.BaseDirectory;
                string archFolder = Environment.Is64BitProcess ? "x64" : "x86";
                string sourcePath = Path.Combine(baseDir, "lib", archFolder, "freetype6.dll");
                if (!File.Exists(sourcePath))
                    return;

                string destPath = Path.Combine(baseDir, "freetype6.dll");
                File.Copy(sourcePath, destPath, overwrite: true);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to ensure SharpFont native dependency: {ex.Message}");
            }
        }

        private static bool TryResolveMsdfAtlasGenExecutable([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? executablePath)
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

        private bool TryGenerateDistanceFieldAtlas(string filePath, string atlasPath, string metadataPath, XRFontImportOptions options, HashSet<uint> characterSet)
        {
            if (!TryResolveMsdfAtlasGenExecutable(out string? executablePath))
            {
                Debug.LogWarning("MSDF font import requested, but msdf-atlas-gen.exe was not found. Run Tools/Dependencies/Get-MsdfAtlasGen.ps1 to install it.");
                return false;
            }

            string? atlasDirectory = Path.GetDirectoryName(atlasPath);
            if (!string.IsNullOrWhiteSpace(atlasDirectory))
                Directory.CreateDirectory(atlasDirectory);

            string? metadataDirectory = Path.GetDirectoryName(metadataPath);
            if (!string.IsNullOrWhiteSpace(metadataDirectory))
                Directory.CreateDirectory(metadataDirectory);

            // Write a charset file so msdf-atlas-gen outputs unicode codepoints
            // (-allglyphs only emits glyph indices which the parser cannot map to characters).
            string charsetPath = Path.Combine(
                atlasDirectory ?? Path.GetTempPath(),
                Path.GetFileNameWithoutExtension(filePath) + ".msdf-charset.txt");
            WriteCharsetFile(charsetPath, characterSet);

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
            };

            startInfo.ArgumentList.Add("-font");
            startInfo.ArgumentList.Add(filePath);
            startInfo.ArgumentList.Add("-charset");
            startInfo.ArgumentList.Add(charsetPath);
            startInfo.ArgumentList.Add("-type");
            startInfo.ArgumentList.Add(GetDistanceFieldToolType(options.AtlasMode));
            startInfo.ArgumentList.Add("-format");
            startInfo.ArgumentList.Add("png");
            startInfo.ArgumentList.Add("-imageout");
            startInfo.ArgumentList.Add(atlasPath);
            startInfo.ArgumentList.Add("-json");
            startInfo.ArgumentList.Add(metadataPath);
            startInfo.ArgumentList.Add("-size");
            startInfo.ArgumentList.Add((options.MsdfFontSize > 0.0f ? options.MsdfFontSize : 48.0f).ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-pxrange");
            startInfo.ArgumentList.Add((options.MsdfPixelRange > 0.0f ? options.MsdfPixelRange : 6.0f).ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-pxpadding");
            startInfo.ArgumentList.Add(MathF.Max(0.0f, options.MsdfInnerPixelPadding).ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-outerpxpadding");
            startInfo.ArgumentList.Add(MathF.Max(0.0f, options.MsdfOuterPixelPadding).ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-coloringstrategy");
            startInfo.ArgumentList.Add("inktrap");
            startInfo.ArgumentList.Add("-pxalign");
            startInfo.ArgumentList.Add("on");
            startInfo.ArgumentList.Add("-scanline");
            startInfo.ArgumentList.Add("-yorigin");
            startInfo.ArgumentList.Add("top");
            startInfo.ArgumentList.Add("-threads");
            startInfo.ArgumentList.Add(Math.Max(0, options.MsdfThreadCount).ToString(System.Globalization.CultureInfo.InvariantCulture));

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            string stdOut = process.StandardOutput.ReadToEnd();
            string stdErr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            // Clean up temporary charset file.
            try { File.Delete(charsetPath); } catch { /* best effort */ }

            if (process.ExitCode != 0)
            {
                Debug.LogWarning($"msdf-atlas-gen failed for '{filePath}' with exit code {process.ExitCode}. {stdErr}".Trim());
                return false;
            }

            if (!TryLoadDistanceFieldMetadata(atlasPath, metadataPath, GetDistanceFieldAtlasType(options.AtlasMode)))
            {
                Debug.LogWarning($"msdf-atlas-gen completed for '{filePath}', but the generated metadata could not be parsed. {stdOut} {stdErr}".Trim());
                return false;
            }

            return true;
        }

        /// <summary>
        /// Writes a charset file with hex Unicode codepoints for msdf-atlas-gen.
        /// </summary>
        private static void WriteCharsetFile(string path, HashSet<uint> characterSet)
        {
            using var writer = new StreamWriter(path, append: false, System.Text.Encoding.ASCII);
            foreach (uint codepoint in characterSet)
            {
                if (codepoint >= 0x20 && codepoint <= 0x10FFFF)
                    writer.WriteLine($"0x{codepoint:X4}");
            }
        }

        private bool TryLoadDistanceFieldMetadata(string atlasPath, string metadataPath, EFontAtlasType atlasType)
        {
            if (!File.Exists(atlasPath) || !File.Exists(metadataPath))
            {
                Debug.WriteAuxiliaryLog(FontDiagnosticsLogName, $"MSDF metadata load failed: missing generated output. atlas='{atlasPath}' exists={File.Exists(atlasPath)}, metadata='{metadataPath}' exists={File.Exists(metadataPath)}");
                return false;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            JsonElement root = document.RootElement;

            if (!root.TryGetProperty("atlas", out JsonElement atlasElement))
                return false;

            float atlasDistanceRange = TryGetSingle(atlasElement, "distanceRange", 0.0f);
            float atlasDistanceRangeMiddle = TryGetSingle(atlasElement, "distanceRangeMiddle", DefaultMsdfDistanceRangeMiddle);
            float atlasFontSize = TryGetSingle(atlasElement, "size", 48.0f);
            float fontScale = atlasFontSize > 0.0f ? atlasFontSize : 48.0f;

            JsonElement glyphContainer = root;
            if (root.TryGetProperty("variants", out JsonElement variants) && variants.ValueKind == JsonValueKind.Array && variants.GetArrayLength() > 0)
                glyphContainer = variants[0];

            if (!glyphContainer.TryGetProperty("glyphs", out JsonElement glyphsElement) || glyphsElement.ValueKind != JsonValueKind.Array)
                return false;

            Dictionary<string, Glyph> glyphs = [];
            foreach (JsonElement glyphElement in glyphsElement.EnumerateArray())
            {
                if (!TryGetGlyphCharacter(glyphElement, out string? character) || string.IsNullOrWhiteSpace(character))
                    continue;

                if (!TryGetBounds(glyphElement, "planeBounds", out float planeLeft, out float planeTop, out float planeRight, out float planeBottom, topDown: true) ||
                    !TryGetBounds(glyphElement, "atlasBounds", out float atlasLeft, out float atlasTop, out float atlasRight, out float atlasBottom, topDown: true))
                {
                    continue;
                }

                float advance = TryGetSingle(glyphElement, "advance", planeRight - planeLeft) * fontScale;
                float left = planeLeft * fontScale;
                float top = -planeTop * fontScale;
                float width = (planeRight - planeLeft) * fontScale;
                float height = (planeBottom - planeTop) * fontScale;

                glyphs[character] = new Glyph(new Vector2(width, height), new Vector2(left, top))
                {
                    Position = new Vector2(atlasLeft, atlasTop),
                    AtlasSize = new Vector2(atlasRight - atlasLeft, atlasBottom - atlasTop),
                    AdvanceX = advance,
                };
            }

            Atlas = CreateAtlasTexture(
                atlasPath,
                atlasType == EFontAtlasType.Mtsdf ? ESizedInternalFormat.Rgba8 : ESizedInternalFormat.Rgb8,
                ETexMinFilter.Linear,
                ETexMagFilter.Linear,
                autoGenerateMipmaps: false);

            Glyphs = glyphs;
            AtlasType = atlasType;
            DistanceRange = atlasDistanceRange;
            // msdf-atlas-gen reports distanceRangeMiddle as an offset from the standard 0.5 edge
            // threshold. The shader expects the absolute pixel value where the glyph edge lies.
            DistanceRangeMiddle = 0.5f + atlasDistanceRangeMiddle;
            LayoutEmSize = fontScale;
            if (glyphs.Count == 0)
                Debug.WriteAuxiliaryLog(FontDiagnosticsLogName, $"MSDF metadata parsed but produced zero glyphs. atlas='{atlasPath}', metadata='{metadataPath}'");
            return glyphs.Count > 0;
        }

        private static bool TryGetGlyphCharacter(JsonElement glyphElement, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? character)
        {
            character = null;
            if (glyphElement.TryGetProperty("unicode", out JsonElement unicodeElement))
            {
                int codepoint = unicodeElement.ValueKind switch
                {
                    JsonValueKind.Number => unicodeElement.GetInt32(),
                    JsonValueKind.String when int.TryParse(unicodeElement.GetString(), out int parsed) => parsed,
                    _ => -1,
                };

                if (codepoint >= 0x20 && codepoint <= 0x10FFFF)
                {
                    character = char.ConvertFromUtf32(codepoint);
                    return true;
                }
            }

            return false;
        }

        private static XRTexture2D CreateAtlasTexture(
            string atlasPath,
            ESizedInternalFormat sizedInternalFormat,
            ETexMinFilter minFilter,
            ETexMagFilter magFilter,
            bool autoGenerateMipmaps)
        {
            using Image<Rgba32> atlasImage = Image.Load<Rgba32>(atlasPath);
            var atlasTexture = new XRTexture2D(atlasImage)
            {
                FilePath = atlasPath,
                OriginalPath = atlasPath,
                Resizable = false,
                AutoGenerateMipmaps = autoGenerateMipmaps,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                MinFilter = minFilter,
                MagFilter = magFilter,
                SizedInternalFormat = sizedInternalFormat,
            };

            if (atlasTexture.Mipmaps is not null)
            {
                foreach (Mipmap2D mipmap in atlasTexture.Mipmaps)
                {
                    if (mipmap.Data is not null)
                        mipmap.Data.PreferCompressedYaml = true;
                }
            }

            return atlasTexture;
        }

        private static bool TryGetBounds(JsonElement element, string propertyName, out float left, out float top, out float right, out float bottom, bool topDown)
        {
            left = top = right = bottom = 0.0f;
            if (!element.TryGetProperty(propertyName, out JsonElement boundsElement))
                return false;

            if (boundsElement.ValueKind == JsonValueKind.Object)
            {
                left = TryGetSingle(boundsElement, "left", 0.0f);
                right = TryGetSingle(boundsElement, "right", 0.0f);
                if (topDown)
                {
                    top = TryGetSingle(boundsElement, "top", 0.0f);
                    bottom = TryGetSingle(boundsElement, "bottom", 0.0f);
                }
                else
                {
                    bottom = TryGetSingle(boundsElement, "bottom", 0.0f);
                    top = TryGetSingle(boundsElement, "top", 0.0f);
                }
                return true;
            }

            if (boundsElement.ValueKind == JsonValueKind.Array)
            {
                float[] values = boundsElement.EnumerateArray().Select(value => value.GetSingle()).ToArray();
                if (values.Length != 4)
                    return false;

                left = values[0];
                if (topDown)
                {
                    top = values[1];
                    right = values[2];
                    bottom = values[3];
                }
                else
                {
                    bottom = values[1];
                    right = values[2];
                    top = values[3];
                }
                return true;
            }

            return false;
        }

        private static float TryGetSingle(JsonElement element, string propertyName, float defaultValue)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement property))
                return defaultValue;

            return property.ValueKind switch
            {
                JsonValueKind.Number => property.GetSingle(),
                JsonValueKind.String when float.TryParse(property.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float parsed) => parsed,
                _ => defaultValue,
            };
        }

        /// <summary>
        /// Retrieves the set of supported characters in a font face.
        /// </summary>
        /// <param name="face"></param>
        /// <returns></returns>
        public static HashSet<uint> GetSupportedCharacters(Face face)
        {
            HashSet<uint> characterSet = [];

            // Select Unicode charmap
            face.SetCharmap(face.CharMaps[0]);

            // Iterate over all characters
            uint code = face.GetFirstChar(out uint glyphIndex);
            while (glyphIndex != 0)
            {
                characterSet.Add(code);
                code = face.GetNextChar(code, out glyphIndex);
            }
            return characterSet;
        }

        /// <summary>
        /// Generates a font atlas texture from a list of characters.
        /// Will save the atlas to a PNG file at the specified path and store glyph coordinates in this set.
        /// </summary>
        /// <param name="typeface"></param>
        /// <param name="characters"></param>
        /// <param name="outputAtlasPath"></param>
        /// <param name="textSize"></param>
        /// <param name="style"></param>
        /// <param name="strokeWidth"></param>
        public void GenerateBitmapFontAtlas(
            SKTypeface typeface,
            List<string> characters,
            string outputAtlasPath,
            float textSize,
            SKPaintStyle style = SKPaintStyle.Fill,
            float strokeWidth = 0.0f,
            bool embolden = false)
        {
            // List to hold glyph data
            List<(string character, Glyph info)> glyphInfos = [];
            List<SKBitmap> glyphBitmaps = [];

            // Create a paint object
            using SKPaint paint = new()
            {
                Color = SKColors.White,
                Style = style,
                StrokeWidth = strokeWidth,
                IsDither = true,
                BlendMode = SKBlendMode.SrcOver,
                IsAntialias = true,
            };

            using SKFont font = new(typeface, textSize);
            font.BaselineSnap = true;
            font.Edging = SKFontEdging.Antialias;
            font.ForceAutoHinting = true;
            font.Subpixel = false;
            font.Embolden = embolden;
            font.Hinting = SKFontHinting.Full;

            // Process each character
            foreach (string character in characters)
            {
                // Get glyph indices
                ushort[] glyphs = new ushort[character.Length]; 
                font.GetGlyphs(character.AsSpan(), glyphs.AsSpan());
                if (glyphs.Length == 0 || glyphs[0] == 0)
                {
                    // Skip characters without glyphs
                    continue;
                }

                float[] widths = new float[glyphs.Length];
                SKRect[] bounds = new SKRect[glyphs.Length];
                font.GetGlyphWidths(glyphs, widths.AsSpan(), bounds.AsSpan(), paint);
                if (bounds.Length > 1)
                    Debug.LogWarning($"Multiple glyphs for character '{character}'");

                SKRect glyphBounds = bounds[0];
                float x = -glyphBounds.Left;
                float y = -glyphBounds.Top;
                int width = (int)Math.Ceiling(glyphBounds.Width);
                int height = (int)Math.Ceiling(glyphBounds.Height);

                if (width == 0 || height == 0)
                {
                    width = 1;
                    height = 1;
                }

                float advance = widths[0] > 0.0f ? widths[0] : width;

                SKBitmap bitmap = new(width, height);
                using (SKCanvas canvas = new(bitmap))
                {
                    canvas.Clear(SKColors.Transparent);
                    canvas.DrawText(character, x, y, font, paint);
                }

                glyphBitmaps.Add(bitmap);
                glyphInfos.Add((character, new(
                    new Vector2(width, height),
                    new Vector2(-glyphBounds.Left, -glyphBounds.Top))
                {
                    AtlasSize = new Vector2(width, height),
                    AdvanceX = advance,
                }));
            }

            // Pack glyphs into an atlas
            int glyphsPerRow = (int)Math.Ceiling(Math.Sqrt(glyphBitmaps.Count));
            int maxGlyphWidth = 0;
            int maxGlyphHeight = 0;

            foreach (var g in glyphInfos)
            {
                if (g.info.Size.X > maxGlyphWidth)
                    maxGlyphWidth = (int)Math.Ceiling(g.info.Size.X);

                if (g.info.Size.Y > maxGlyphHeight)
                    maxGlyphHeight = (int)Math.Ceiling(g.info.Size.Y);
            }

            int glyphCellWidth = maxGlyphWidth + (BitmapAtlasPadding * 2);
            int glyphCellHeight = maxGlyphHeight + (BitmapAtlasPadding * 2);

            int atlasWidth = glyphCellWidth * glyphsPerRow;
            int numRows = (int)Math.Ceiling((double)glyphBitmaps.Count / glyphsPerRow);
            int atlasHeight = glyphCellHeight * numRows;

            using SKBitmap atlasBitmap = new(atlasWidth, atlasHeight);
            using (SKCanvas atlasCanvas = new(atlasBitmap))
            {
                atlasCanvas.Clear(SKColors.Transparent);

                // Draw each glyph onto the atlas
                for (int i = 0; i < glyphBitmaps.Count; i++)
                {
                    int row = i / glyphsPerRow;
                    int col = i % glyphsPerRow;

                    int x = (col * glyphCellWidth) + BitmapAtlasPadding;
                    int y = (row * glyphCellHeight) + BitmapAtlasPadding;

                    atlasCanvas.DrawBitmap(glyphBitmaps[i], x, y);
                    glyphInfos[i].info.Position = new Vector2(x, y);
                }
            }

            // Save the atlas texture to the (cache) output path.
            string? outputDir = Path.GetDirectoryName(outputAtlasPath);
            if (!string.IsNullOrWhiteSpace(outputDir))
                Directory.CreateDirectory(outputDir);

            using (var image = SKImage.FromBitmap(atlasBitmap))
            using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
            using (var stream = new FileStream(outputAtlasPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                data.SaveTo(stream);
            }

            Atlas = CreateAtlasTexture(
                outputAtlasPath,
                ESizedInternalFormat.R8,
                ETexMinFilter.LinearMipmapLinear,
                ETexMagFilter.Linear,
                autoGenerateMipmaps: true);

            Glyphs = glyphInfos.ToDictionary(g => g.character, g => g.info);
            AtlasType = EFontAtlasType.Bitmap;
            DistanceRange = 0.0f;
            DistanceRangeMiddle = DefaultMsdfDistanceRangeMiddle;
            LayoutEmSize = textSize > 0.0f ? textSize : DefaultBitmapFontDrawSize;

            foreach (var bitmap in glyphBitmaps)
                bitmap.Dispose();
        }

        public enum EWrapMode
        {
            /// <summary>
            /// No wrapping is applied.
            /// </summary>
            None,
            /// <summary>
            /// Wrap at the character level.
            /// </summary>
            Character,
            /// <summary>
            /// Wrap at the word level.
            /// </summary>
            Word,
        }

        /// <summary>
        /// Retrieves quads for rendering a string of text.
        /// </summary>
        /// <param name="str"></param>
        /// <param name="quads"></param>
        /// <param name="fontSize"></param>
        /// <param name="spacing"></param>
        public void GetQuads(
            string? str,
            List<(Vector4 transform, Vector4 uvs)> quads,
            float fontSize,
            float spacing = 0.0f,
            float lineSpacing = 5.0f)
            => GetQuads(
                str,
                quads,
                Vector2.Zero,
                fontSize,
                spacing,
                lineSpacing);

        /// <summary>
        /// Retrieves quads for rendering a string of text.
        /// </summary>
        /// <param name="str"></param>
        /// <param name="quads"></param>
        /// <param name="offset"></param>
        /// <param name="fontSize"></param>
        /// <param name="spacing"></param>
        public void GetQuads(
            string? str,
            List<(Vector4 transform, Vector4 uvs)> quads,
            Vector2 offset,
            float fontSize,
            float spacing = 0.0f,
            float lineSpacing = 5.0f)
        {
            if (!EnsureLayoutResourcesReady())
            {
                quads.Clear();
                return;
            }

            GetQuads(
                str,
                Glyphs,
                new IVector2((int)Atlas!.Width, (int)Atlas.Height),
                LayoutEmSize,
                quads,
                offset,
                fontSize,
                spacing,
                lineSpacing);
        }

        private static bool TryReadNextRune(ReadOnlySpan<char> text, ref int charIndex, out Rune rune, out bool last)
        {
            while (charIndex < text.Length)
            {
                OperationStatus status = Rune.DecodeFromUtf16(text[charIndex..], out rune, out int charsConsumed);
                if (status == OperationStatus.Done)
                {
                    last = charIndex + charsConsumed >= text.Length;
                    charIndex += charsConsumed;
                    return true;
                }

                charIndex++;
            }

            rune = default;
            last = true;
            return false;
        }

        /// <summary>
        /// Retrieves quads for rendering a string of text.
        /// </summary>
        /// <param name="str"></param>
        /// <param name="glyphs"></param>
        /// <param name="atlasSize"></param>
        /// <param name="quads"></param>
        /// <param name="offset"></param>
        /// <param name="fontSize"></param>
        /// <param name="spacing"></param>
        private static void GetQuads(
            string? str,
            Dictionary<string, Glyph> glyphs,
            IVector2 atlasSize,
            float layoutEmSize,
            List<(Vector4 transform, Vector4 uvs)> quads,
            Vector2 offset,
            float fontSize,
            float spacing = 0.0f)
        {
            quads.Clear();
            if (str is null)
                return;

            ReadOnlySpan<char> text = str.AsSpan();
            float xOffset = offset.X;
            for (int charIndex = 0; charIndex < text.Length;)
            {
                if (!TryReadNextRune(text, ref charIndex, out Rune rune, out bool last))
                    break;

                string character = rune.ToString();
                if (!glyphs.TryGetValue(character, out Glyph glyph))
                {
                    // Handle missing glyphs (e.g., skip or substitute)
                    continue;
                }

                float scale = fontSize / layoutEmSize;
                float translateX = (xOffset + glyph.Bearing.X) * scale;
                float translateY = (offset.Y + glyph.Bearing.Y) * scale;
                float scaleX = glyph.Size.X * scale;
                float scaleY = -glyph.Size.Y * scale;

                Vector4 transform = new(
                    translateX,
                    translateY,
                    scaleX,
                    scaleY);

                float u0 = glyph.Position.X / atlasSize.X;
                float v0 = glyph.Position.Y / atlasSize.Y;
                float u1 = (glyph.Position.X + glyph.EffectiveAtlasSize.X) / atlasSize.X;
                float v1 = (glyph.Position.Y + glyph.EffectiveAtlasSize.Y) / atlasSize.Y;

                // Add UVs in the order matching the quad vertices
                // Assuming quad vertices are defined in this order:
                // Bottom-left (0, 0)
                // Bottom-right (1, 0)
                // Top-right (1, 1)
                // Top-left (0, 1)
                Vector4 uvs = new(u0, v0, u1, v1); // Bottom-left to Top-right
                
                quads.Add((transform, uvs));

                xOffset += glyph.EffectiveAdvance;
                if (!last)
                    xOffset += spacing;
            }
        }

        /// <summary>
        /// Retrieves quads for rendering a string of text.
        /// </summary>
        /// <param name="str"></param>
        /// <param name="quads"></param>
        /// <param name="fontSize"></param>
        /// <param name="maxWidth"></param>
        /// <param name="maxHeight"></param>
        /// <param name="wrap"></param>
        /// <param name="spacing"></param>
        public void GetQuads(
            string? str,
            List<(Vector4 transform, Vector4 uvs)> quads,
            float? fontSize,
            float maxWidth,
            float maxHeight,
            EWrapMode wrap = EWrapMode.None,
            float spacing = 0.0f,
            float lineSpacing = 5.0f)
            => GetQuads(
                str,
                quads,
                Vector2.Zero,
                fontSize,
                maxWidth,
                maxHeight,
                wrap,
                spacing,
                lineSpacing);

        private bool EnsureLayoutResourcesReady()
        {
            if (Glyphs is not null && Atlas is not null)
                return true;

            string recoveryKey = !string.IsNullOrWhiteSpace(OriginalPath)
                ? OriginalPath!
                : !string.IsNullOrWhiteSpace(FilePath)
                    ? FilePath!
                    : ID != Guid.Empty
                        ? ID.ToString()
                        : Name ?? nameof(FontGlyphSet);

            if (MissingAtlasRecoveryAttemptedKeys.TryAdd(recoveryKey, 0))
            {
                try
                {
                    string? sourcePath = !string.IsNullOrWhiteSpace(OriginalPath) ? OriginalPath : FilePath;
                    if (!string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath))
                    {
                        XRFontImportOptions options = ResolveImportOptions(sourcePath);
                        ImportFont(sourcePath, options, auxiliaryFileName => ResolveFallbackAuxiliaryPath(sourcePath, auxiliaryFileName));
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteAuxiliaryLog(FontDiagnosticsLogName, $"Font layout recovery failed for '{OriginalPath ?? FilePath ?? Name ?? "<unknown>"}': {ex}");
                }
            }

            return Glyphs is not null && Atlas is not null;
        }

        /// <summary>
        /// Retrieves quads for rendering a string of text.
        /// </summary>
        /// <param name="str"></param>
        /// <param name="quads"></param>
        /// <param name="offset"></param>
        /// <param name="fontSize"></param>
        /// <param name="maxWidth"></param>
        /// <param name="maxHeight"></param>
        /// <param name="wrap"></param>
        /// <param name="spacing"></param>
        public void GetQuads(
            string? str,
            List<(Vector4 transform, Vector4 uvs)> quads,
            Vector2 offset,
            float? fontSize,
            float maxWidth,
            float maxHeight,
            EWrapMode wrap = EWrapMode.None,
            float spacing = 0.0f,
            float lineSpacing = 5.0f)
        {
            if (!EnsureLayoutResourcesReady())
            {
                quads.Clear();
                return;
            }

            GetQuads(
                str,
                Glyphs,
                new IVector2((int)Atlas!.Width, (int)Atlas.Height),
                LayoutEmSize,
                quads,
                offset,
                fontSize,
                maxWidth,
                maxHeight,
                wrap,
                spacing,
                lineSpacing);
        }

        /// <summary>
        /// Retrieves quads for rendering a string of text.
        /// </summary>
        /// <param name="str"></param>
        /// <param name="glyphs"></param>
        /// <param name="atlasSize"></param>
        /// <param name="quads"></param>
        /// <param name="offset"></param>
        /// <param name="fontSize"></param>
        /// <param name="maxWidth"></param>
        /// <param name="maxHeight"></param>
        /// <param name="wrap"></param>
        /// <param name="spacing"></param>
        private static void GetQuads(
            string? str,
            Dictionary<string, Glyph> glyphs,
            IVector2 atlasSize,
            float layoutEmSize,
            List<(Vector4 transform, Vector4 uvs)> quads,
            Vector2 offset,
            float? fontSize,
            float maxWidth,
            float maxHeight,
            EWrapMode wrap = EWrapMode.None,
            float spacing = 0.0f,
            float lineSpacing = 5.0f)
        {
            quads.Clear();
            if (str is null)
                return;

            float xOffset = offset.X;
            float yOffset = offset.Y;
            float lineHeight = fontSize ?? 0.0f;
            float spaceWidth = glyphs.TryGetValue(" ", out Glyph? spaceGlyph) ? MathF.Max(spaceGlyph.EffectiveAdvance, layoutEmSize * 0.25f) : layoutEmSize * 0.25f;

            // Track line breaks for a single deferred Y-shift pass instead of
            // shifting all previous quads on every newline/wrap (was O(n*k)).
            int lineBreakCount = 0;
            float lineBreakShiftTotal = 0.0f;
            Span<float> lineBreakHeights = str.Length <= 256
                ? stackalloc float[Math.Min(str.Length, 256)]
                : new float[str.Length];
            Span<int> lineBreakQuadIndices = str.Length <= 256
                ? stackalloc int[Math.Min(str.Length, 256)]
                : new int[str.Length];

            float scale = (fontSize ?? 1.0f) / layoutEmSize;
            ReadOnlySpan<char> text = str.AsSpan();
            for (int charIndex = 0; charIndex < text.Length;)
            {
                int runeStart = charIndex;
                if (!TryReadNextRune(text, ref charIndex, out Rune rune, out bool last))
                    break;

                bool first = runeStart == 0;

                if (rune.Value == ' ')
                {
                    xOffset += spaceWidth;
                    if (!last)
                        xOffset += spacing;
                    continue;
                }
                if (rune.Value == '\n')
                {
                    xOffset = offset.X;
                    lineBreakHeights[lineBreakCount] = lineHeight;
                    lineBreakQuadIndices[lineBreakCount] = quads.Count;
                    lineBreakShiftTotal += lineHeight + lineSpacing;
                    lineBreakCount++;
                    if (fontSize is null)
                        lineHeight = 0.0f;
                    continue;
                }

                string character = rune.ToString();
                if (!glyphs.TryGetValue(character, out Glyph glyph))
                {
                    // Handle missing glyphs (e.g., skip or substitute)
                    continue;
                }

                float translateX = (xOffset + glyph.Bearing.X) * scale;
                if (first)
                {
                    xOffset -= glyph.Bearing.X;
                    translateX = 0.0f;
                }
                float translateY = (yOffset + glyph.Bearing.Y) * scale;
                float scaleX = glyph.Size.X * scale;
                float scaleY = -glyph.Size.Y * scale;

                if (wrap != EWrapMode.None && (translateX + scaleX) > maxWidth && maxWidth > 0.0f)
                {
                    xOffset = offset.X;
                    lineBreakHeights[lineBreakCount] = lineHeight;
                    lineBreakQuadIndices[lineBreakCount] = quads.Count;
                    lineBreakShiftTotal += lineHeight + lineSpacing;
                    lineBreakCount++;
                    if (fontSize is null)
                        lineHeight = 0.0f;
                    translateX = (xOffset + glyph.Bearing.X) * scale;
                    translateY = (yOffset + glyph.Bearing.Y) * scale;
                }

                Vector4 transform = new(
                    translateX,
                    translateY,
                    scaleX,
                    scaleY);

                float u0 = glyph.Position.X / atlasSize.X;
                float v0 = glyph.Position.Y / atlasSize.Y;
                float u1 = (glyph.Position.X + glyph.EffectiveAtlasSize.X) / atlasSize.X;
                float v1 = (glyph.Position.Y + glyph.EffectiveAtlasSize.Y) / atlasSize.Y;

                Vector4 uvs = new(u0, v0, u1, v1);

                quads.Add((transform, uvs));

                xOffset += glyph.EffectiveAdvance;
                if (!last)
                    xOffset += spacing;

                if (fontSize is null)
                    lineHeight = Math.Max(lineHeight, glyph.Size.Y * scale);
            }

            // Apply all Y-shifts in a single O(n) pass.
            // Text builds bottom-up: last line stays at yOffset, earlier lines shift up.
            if (lineBreakCount > 0)
            {
                int segStart = 0;
                float shift = lineBreakShiftTotal;
                for (int b = 0; b < lineBreakCount; b++)
                {
                    int segEnd = lineBreakQuadIndices[b];
                    for (int j = segStart; j < segEnd; j++)
                    {
                        var (t, u) = quads[j];
                        t.Y += shift;
                        quads[j] = (t, u);
                    }
                    shift -= lineBreakHeights[b] + lineSpacing;
                    segStart = segEnd;
                }
            }

            if (fontSize is null)
            {
                float maxX = xOffset;
                float maxY = yOffset + lineHeight;
                float boundsX = maxWidth;
                float boundsY = maxHeight;
                float widthScale = boundsX / maxX;
                float heightScale = boundsY / maxY;
                float minScale = Math.Min(widthScale, heightScale);
                for (int i = 0; i < quads.Count; i++)
                {
                    Vector4 transform = quads[i].transform;
                    transform.X *= minScale;
                    transform.Y *= minScale;
                    transform.Z *= minScale;
                    transform.W *= minScale;
                    quads[i] = (transform, quads[i].uvs);
                }
            }
        }

        public float CalculateFontSize(
            string? str,
            Vector2 offset,
            Vector2 bounds,
            float spacing = 0.0f)
        {
            if (Glyphs is null)
                throw new InvalidOperationException("Glyphs are not initialized.");

            return CalculateFontSize(str, Glyphs, offset, bounds, spacing, LayoutEmSize);
        }
        public static float CalculateFontSize(
            string? str,
            Dictionary<string, Glyph> glyphs,
            Vector2 offset,
            Vector2 bounds,
            float spacing = 0.0f,
            float layoutEmSize = DefaultBitmapFontDrawSize)
        {
            if (str is null)
                return 0.0f;

            float maxHeight = 0.0f;
            float xOffset = offset.X;
            float spaceWidth = glyphs.TryGetValue(" ", out Glyph? spaceGlyph) ? MathF.Max(spaceGlyph.EffectiveAdvance, layoutEmSize * 0.25f) : layoutEmSize * 0.25f;
            for (int i = 0; i < str.Length; i++)
            {
                bool last = i == str.Length - 1;
                char ch = str[i];
                string character = ch.ToString();
                if (character == " ")
                {
                    xOffset += spaceWidth;
                    if (!last)
                        xOffset += spacing;
                    continue;
                }
                if (!glyphs.ContainsKey(character))
                {
                    // Handle missing glyphs (e.g., skip or substitute)
                    continue;
                }

                Glyph glyph = glyphs[character];
                float scale = 1.0f / layoutEmSize;

                xOffset += glyph.EffectiveAdvance;
                if (!last)
                    xOffset += spacing;
                maxHeight = Math.Max(maxHeight, glyph.Size.Y * scale);
            }

            float widthScale = bounds.X / xOffset;
            float heightScale = bounds.Y / maxHeight;
            return Math.Min(widthScale, heightScale);
        }

        public static FontGlyphSet LoadEngineFont(string folderName, string fontName)
            => LoadEngineFont(folderName, fontName, (XRFontImportOptions?)null);

        public static FontGlyphSet LoadEngineFont(string folderName, string fontName, EFontAtlasImportMode? atlasModeOverride)
            => LoadEngineFont(folderName, fontName, CreateAtlasModeOverrideOptions(atlasModeOverride));

        public static FontGlyphSet LoadEngineFont(string folderName, string fontName, XRFontImportOptions? importOptionsOverride)
        {
            string path = Engine.Assets.ResolveEngineAssetPath(
                Engine.Rendering.Constants.EngineFontsCommonFolderName,
                folderName,
                fontName);

            string extension = Path.GetExtension(path);
            if (extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".otf", StringComparison.OrdinalIgnoreCase))
            {
                XRFontImportOptions resolvedImportOptions = ResolveDirectEngineFontImportOptions(path, importOptionsOverride);
                string directCacheKey = BuildDirectEngineFontCacheKey(path, resolvedImportOptions);
                FontGlyphSet directFont = GetOrLoadDirectEngineFont(directCacheKey, path, resolvedImportOptions);
                Debug.WriteAuxiliaryLog(FontDiagnosticsLogName, $"LoadEngineFont (direct): folder='{folderName}', font='{fontName}', atlasMode={resolvedImportOptions.AtlasMode}, atlasType={directFont.AtlasType}, glyphs={directFont.Glyphs?.Count ?? 0}, atlas='{directFont.Atlas?.OriginalPath ?? directFont.Atlas?.FilePath ?? "<null>"}', assetPath='{directFont.FilePath ?? "<null>"}', originalPath='{directFont.OriginalPath ?? "<null>"}', layoutEm={directFont.LayoutEmSize}, range={directFont.DistanceRange}, middle={directFont.DistanceRangeMiddle}");
                return directFont;
            }

            FontGlyphSet font = Engine.Assets.Load<FontGlyphSet>(path)
                ?? throw new FileNotFoundException($"Unable to find engine file at {path}");

            if (ShouldForceMsdfReload(font, path) && ForcedReloadAttemptedPaths.TryAdd(path, 0))
            {
                Debug.WriteAuxiliaryLog(FontDiagnosticsLogName, $"LoadEngineFont: forcing one-time reload for '{path}' because the cached in-memory font is still bitmap.");
                EvictLoadedAsset(font, path);

                FontGlyphSet? reloaded = Engine.Assets.Load<FontGlyphSet>(path, JobPriority.Highest, true);
                if (reloaded is not null)
                    font = reloaded;

                Debug.WriteAuxiliaryLog(FontDiagnosticsLogName, $"LoadEngineFont: forced reload result for '{path}' => atlasType={font.AtlasType}, glyphs={font.Glyphs?.Count ?? 0}, layoutEm={font.LayoutEmSize}, range={font.DistanceRange}, middle={font.DistanceRangeMiddle}");
            }

            Debug.WriteAuxiliaryLog(FontDiagnosticsLogName, $"LoadEngineFont: folder='{folderName}', font='{fontName}', atlasType={font.AtlasType}, glyphs={font.Glyphs?.Count ?? 0}, atlas='{font.Atlas?.OriginalPath ?? font.Atlas?.FilePath ?? "<null>"}', assetPath='{font.FilePath ?? "<null>"}', originalPath='{font.OriginalPath ?? "<null>"}', layoutEm={font.LayoutEmSize}, range={font.DistanceRange}, middle={font.DistanceRangeMiddle}");
            return font;
        }

        public static FontGlyphSet LoadDefaultFontBitmap()
            => LoadEngineFont(
                Engine.Rendering.Settings.DefaultFontFolder,
                Engine.Rendering.Settings.DefaultFontFileName,
                CreateBitmapImportOptions(DefaultBitmapMipmapFontDrawSize));

        public static FontGlyphSet LoadDefaultUIFont()
                => LoadDefaultUIFontBitmap();

        public static FontGlyphSet LoadDefaultUIIconFont()
            => TryLoadWindowsUIFont("seguisym.ttf") ?? LoadDefaultUIFontBitmap();

        public static FontGlyphSet LoadDefaultUIEmojiFont()
            => TryLoadWindowsUIFont("seguiemj.ttf")
            ?? TryLoadWindowsUIFont("seguisym.ttf")
            ?? LoadDefaultUIFontBitmap();

        public static FontGlyphSet LoadDefaultUIFontBitmap()
            => LoadEngineFont(
                Engine.Rendering.Settings.DefaultFontFolder,
                    Engine.Rendering.Settings.DefaultFontFileName,
                CreateBitmapImportOptions(DefaultBitmapMipmapFontDrawSize));

        public static FontGlyphSet LoadDefaultUIFontMtsdf()
            => LoadEngineFont(
                Engine.Rendering.Settings.DefaultFontFolder,
                Engine.Rendering.Settings.DefaultFontFileName,
                CreateUiMtsdfImportOptions());

        public static FontGlyphSet LoadDefaultFontMsdf()
            => LoadEngineFont(
                Engine.Rendering.Settings.DefaultFontFolder,
                Engine.Rendering.Settings.DefaultFontFileName,
                CreateMsdfImportOptions(DefaultWorldMtsdfFontSize, DefaultWorldMtsdfPixelRange));

        public static FontGlyphSet LoadDefaultFontMtsdf()
            => LoadEngineFont(
                Engine.Rendering.Settings.DefaultFontFolder,
                Engine.Rendering.Settings.DefaultFontFileName,
                CreateWorldMtsdfImportOptions());

        private static FontGlyphSet? TryLoadWindowsUIFont(string fileName)
        {
            if (!OperatingSystem.IsWindows())
                return null;

            string fontsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            if (string.IsNullOrWhiteSpace(fontsDirectory))
                return null;

            string path = Path.Combine(fontsDirectory, fileName);
            if (!File.Exists(path))
                return null;

            XRFontImportOptions options = CreateBitmapImportOptions(DefaultBitmapMipmapFontDrawSize);
            string cacheKey = BuildDirectEngineFontCacheKey(path, options);

            try
            {
                return GetOrLoadDirectEngineFont(cacheKey, path, options);
            }
            catch (Exception ex)
            {
                Debug.WriteAuxiliaryLog(FontDiagnosticsLogName, $"LoadWindowsUIFont failed for '{path}': {ex.Message}");
                return null;
            }
        }

        private static string BuildDirectEngineFontCacheKey(string path, XRFontImportOptions importOptions)
            => $"{path}|{BuildImportProfileKey(importOptions)}";

        private static FontGlyphSet GetOrLoadDirectEngineFont(string cacheKey, string path, XRFontImportOptions importOptions)
        {
            Lazy<FontGlyphSet> lazyFont = DirectEngineFontCache.GetOrAdd(
                cacheKey,
                _ => new Lazy<FontGlyphSet>(
                    () => LoadEngineFontDirect(path, importOptions),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            try
            {
                return lazyFont.Value;
            }
            catch
            {
                DirectEngineFontCache.TryRemove(cacheKey, out _);
                throw;
            }
        }

        private static FontGlyphSet LoadEngineFontDirect(string path, XRFontImportOptions importOptions)
        {
            bool logTiming = Engine.StartingUp || Engine.StartupPresentationEnabled;
            var stopwatch = logTiming ? System.Diagnostics.Stopwatch.StartNew() : null;
            string importProfileKey = BuildImportProfileKey(importOptions);
            string? cacheDirectory = ResolveEngineFontCacheDirectory(path, importProfileKey);
            Debug.WriteAuxiliaryLog(FontDiagnosticsLogName, $"LoadEngineFontDirect: path='{path}', cacheDir='{cacheDirectory ?? "<null>"}', cacheAssetPathMode='asset-manager-variant'");

            FontGlyphSet font = Engine.Assets.Load3rdPartyVariantWithCache<FontGlyphSet>(path, importOptions, importProfileKey, JobPriority.Highest, bypassJobThread: true)
                ?? throw new FileNotFoundException($"Unable to import engine font at {path}");

            // Safety net: if the cached font loaded without an atlas or with a blank atlas
            // (e.g., stale or partially deserialized cache), evict and reimport fresh.
            if (font.Atlas is null || font.Atlas.Mipmaps is { Length: 0 })
            {
                Debug.WriteAuxiliaryLog(FontDiagnosticsLogName, $"LoadEngineFontDirect: cached font at '{path}' has a missing or blank atlas. atlasNull={font.Atlas is null}, mipmaps={font.Atlas?.Mipmaps?.Length ?? -1}. Evicting stale cache and reimporting.");
                EvictLoadedAsset(font, path);
                font = Engine.Assets.Load3rdPartyVariantWithCache<FontGlyphSet>(path, importOptions, importProfileKey, JobPriority.Highest, bypassJobThread: true)
                    ?? throw new FileNotFoundException($"Unable to reimport engine font at {path}");
            }

            font.Name ??= Path.GetFileNameWithoutExtension(path);
            font.FilePath = path;
            font.OriginalPath = path;

            stopwatch?.Stop();
            if (stopwatch is not null)
            {
                Debug.Out(
                    "[StartupUI] FontGlyphSet.LoadEngineFontDirect completed in {0:F1} ms for '{1}'.",
                    stopwatch.Elapsed.TotalMilliseconds,
                    Path.GetFileName(path));
            }

            return font;
        }

        private static string? ResolveEngineFontCacheDirectory(string sourcePath, string? importProfileKey = null)
        {
            string engineAssetsPath = Path.GetFullPath(Engine.Assets.EngineAssetsPath);
            string normalizedSource = Path.GetFullPath(sourcePath);
            string relativePath = Path.GetRelativePath(engineAssetsPath, normalizedSource);
            if (relativePath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
                return null;

            string? relativeDirectory = Path.GetDirectoryName(relativePath);
            string? cacheRoot = Engine.Assets.GameCachePath;
            if (string.IsNullOrWhiteSpace(cacheRoot))
            {
                string? projectRoot = Path.GetDirectoryName(Path.GetFullPath(Engine.Assets.GameAssetsPath));
                if (!string.IsNullOrWhiteSpace(projectRoot))
                    cacheRoot = Path.Combine(projectRoot, "Cache");
            }

            if (string.IsNullOrWhiteSpace(cacheRoot))
                return null;

            string engineCacheRoot = Path.Combine(cacheRoot, "Engine");
            string relativeCacheDirectory = string.IsNullOrWhiteSpace(relativeDirectory)
                ? engineCacheRoot
                : Path.Combine(engineCacheRoot, relativeDirectory);

            return string.IsNullOrWhiteSpace(importProfileKey)
                ? relativeCacheDirectory
                : Path.Combine(relativeCacheDirectory, importProfileKey);
        }

        private static bool ShouldForceMsdfReload(FontGlyphSet font, string path)
        {
            if (font.AtlasType == EFontAtlasType.Msdf)
                return false;

            string extension = Path.GetExtension(path);
            if (!extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".otf", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            XRFontImportOptions options = ResolveImportOptions(path);
            return options.AtlasMode != EFontAtlasImportMode.Bitmap;
        }

        private static void EvictLoadedAsset(FontGlyphSet font, string path)
        {
            Engine.Assets.LoadedAssetsByPathInternal.TryRemove(path, out _);

            if (!string.IsNullOrWhiteSpace(font.OriginalPath))
                Engine.Assets.LoadedAssetsByOriginalPathInternal.TryRemove(font.OriginalPath, out _);

            if (font.ID != Guid.Empty)
                Engine.Assets.LoadedAssetsByIDInternal.TryRemove(font.ID, out _);
        }

        public static async Task<FontGlyphSet> LoadEngineFontAsync(string folderName, string fontName)
            => await Engine.Assets.LoadEngineAssetAsync<FontGlyphSet>(
                Engine.Rendering.Constants.EngineFontsCommonFolderName,
                folderName,
                fontName);

        public static FontGlyphSet LoadDefaultFont()
            => LoadDefaultFontMtsdf();

        private static XRFontImportOptions? CreateAtlasModeOverrideOptions(EFontAtlasImportMode? atlasModeOverride)
            => atlasModeOverride.HasValue ? new XRFontImportOptions { AtlasMode = atlasModeOverride.Value } : null;

        private static XRFontImportOptions ResolveDirectEngineFontImportOptions(string path, XRFontImportOptions? importOptionsOverride)
        {
            XRFontImportOptions importOptions = CloneImportOptions(ResolveImportOptions(path));
            if (importOptionsOverride is null)
                return importOptions;

            importOptions.AtlasMode = importOptionsOverride.AtlasMode;
            importOptions.BitmapFontDrawSize = importOptionsOverride.BitmapFontDrawSize;
            importOptions.MsdfFontSize = importOptionsOverride.MsdfFontSize;
            importOptions.MsdfPixelRange = importOptionsOverride.MsdfPixelRange;
            importOptions.MsdfInnerPixelPadding = importOptionsOverride.MsdfInnerPixelPadding;
            importOptions.MsdfOuterPixelPadding = importOptionsOverride.MsdfOuterPixelPadding;
            importOptions.MsdfThreadCount = importOptionsOverride.MsdfThreadCount;
            importOptions.AllowBitmapFallback = importOptionsOverride.AllowBitmapFallback;
            return importOptions;
        }

        private static XRFontImportOptions CloneImportOptions(XRFontImportOptions source)
            => new()
            {
                AtlasMode = source.AtlasMode,
                BitmapFontDrawSize = source.BitmapFontDrawSize,
                MsdfFontSize = source.MsdfFontSize,
                MsdfPixelRange = source.MsdfPixelRange,
                MsdfInnerPixelPadding = source.MsdfInnerPixelPadding,
                MsdfOuterPixelPadding = source.MsdfOuterPixelPadding,
                MsdfThreadCount = source.MsdfThreadCount,
                AllowBitmapFallback = source.AllowBitmapFallback,
            };

        private static XRFontImportOptions CreateBitmapImportOptions(float drawSize)
            => new()
            {
                AtlasMode = EFontAtlasImportMode.Bitmap,
                BitmapFontDrawSize = drawSize,
            };

        private static XRFontImportOptions CreateMsdfImportOptions(float fontSize, float pixelRange)
            => new()
            {
                AtlasMode = EFontAtlasImportMode.Msdf,
                MsdfFontSize = fontSize,
                MsdfPixelRange = pixelRange,
                MsdfOuterPixelPadding = 4.0f,
            };

        private static XRFontImportOptions CreateWorldMtsdfImportOptions()
            => new()
            {
                AtlasMode = EFontAtlasImportMode.Mtsdf,
                MsdfFontSize = DefaultWorldMtsdfFontSize,
                MsdfPixelRange = DefaultWorldMtsdfPixelRange,
                MsdfOuterPixelPadding = 4.0f,
            };

        private static XRFontImportOptions CreateUiMtsdfImportOptions()
            => new()
            {
                AtlasMode = EFontAtlasImportMode.Mtsdf,
                MsdfFontSize = DefaultUiMtsdfFontSize,
                MsdfPixelRange = DefaultUiMtsdfPixelRange,
                MsdfOuterPixelPadding = 4.0f,
            };

        private static string GetDistanceFieldToolType(EFontAtlasImportMode atlasMode)
            => atlasMode == EFontAtlasImportMode.Msdf ? "msdf" : "mtsdf";

        private static string GetDistanceFieldAuxiliarySuffix(EFontAtlasImportMode atlasMode)
            => atlasMode == EFontAtlasImportMode.Msdf ? "msdf" : "mtsdf";

        private static EFontAtlasType GetDistanceFieldAtlasType(EFontAtlasImportMode atlasMode)
            => atlasMode == EFontAtlasImportMode.Msdf ? EFontAtlasType.Msdf : EFontAtlasType.Mtsdf;

        private static string BuildImportProfileKey(XRFontImportOptions importOptions)
            => string.Join(
                "_",
                $"mode-{importOptions.AtlasMode.ToString().ToLowerInvariant()}",
                $"bmp-{FormatProfileFloat(importOptions.BitmapFontDrawSize)}",
                $"size-{FormatProfileFloat(importOptions.MsdfFontSize)}",
                $"range-{FormatProfileFloat(importOptions.MsdfPixelRange)}",
                $"inner-{FormatProfileFloat(importOptions.MsdfInnerPixelPadding)}",
                $"outer-{FormatProfileFloat(importOptions.MsdfOuterPixelPadding)}",
                $"threads-{importOptions.MsdfThreadCount}",
                $"fallback-{(importOptions.AllowBitmapFallback ? 1 : 0)}");

        private static string FormatProfileFloat(float value)
            => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture).Replace('.', '_');

        public static async Task<FontGlyphSet> LoadDefaultFontAsync()
            => await LoadEngineFontAsync(
                Engine.Rendering.Settings.DefaultFontFolder,
                Engine.Rendering.Settings.DefaultFontFileName);

        public Vector2 MeasureString(string str, float fontSize)
        {
            if (Glyphs is null)
                throw new InvalidOperationException("Glyphs are not initialized.");

            return MeasureString(str, Glyphs, fontSize, LayoutEmSize);
        }

        public static Vector2 MeasureString(string str, Dictionary<string, Glyph> glyphs, float fontSize, float layoutEmSize = DefaultBitmapFontDrawSize)
        {
            float width = 0.0f;
            float height = 0.0f;
            float xOffset = 0.0f;
            float spaceWidth = glyphs.TryGetValue(" ", out Glyph? spaceGlyph) ? MathF.Max(spaceGlyph.EffectiveAdvance, layoutEmSize * 0.25f) : layoutEmSize * 0.25f;
            for (int i = 0; i < str.Length; i++)
            {
                char ch = str[i];
                string character = ch.ToString();
                if (character == " ")
                {
                    xOffset += spaceWidth;
                    width = Math.Max(width, xOffset * (fontSize / layoutEmSize));
                    continue;
                }
                if (!glyphs.ContainsKey(character))
                {
                    // Handle missing glyphs (e.g., skip or substitute)
                    continue;
                }
                Glyph glyph = glyphs[character];
                float scale = fontSize / layoutEmSize;
                float left = (xOffset + glyph.Bearing.X) * scale;
                float right = left + glyph.Size.X * scale;
                width = Math.Max(width, right);
                height = Math.Max(height, glyph.Size.Y * scale);
                xOffset += glyph.EffectiveAdvance;
            }
            return new Vector2(width, height);
        }

        [MemoryPackable]
        public partial class Glyph
        {
            public Vector2 Position;
            public Vector2 AtlasSize;
            public Vector2 Size;
            public Vector2 Bearing;
            public float AdvanceX;

            /// <summary>
            /// Returns AtlasSize if non-zero, otherwise falls back to Size
            /// for backward compatibility with old serialized data.
            /// </summary>
            [MemoryPackIgnore]
            public Vector2 EffectiveAtlasSize => AtlasSize != Vector2.Zero ? AtlasSize : Size;

            /// <summary>
            /// Returns AdvanceX if positive, otherwise falls back to Size.X
            /// for backward compatibility with old serialized data.
            /// </summary>
            [MemoryPackIgnore]
            public float EffectiveAdvance => AdvanceX > 0.0f ? AdvanceX : Size.X;

            [MemoryPackConstructor]
            public Glyph() { }
            public Glyph(Vector2 size, Vector2 bearing)
            {
                AtlasSize = size;
                Size = size;
                Bearing = bearing;
                AdvanceX = size.X;
            }
        }
    }
}