using SharpFont;
using SkiaSharp;
using System.Numerics;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;

namespace XREngine.Rendering
{
    [XR3rdPartyExtensions("otf", "ttf")]
    public class FontGlyphSet : XRAsset
    {
        private const float FontDrawSize = 100.0f;

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

        protected override void Reload3rdParty(string path)
            => Load3rdParty(path);
        public override bool Load3rdParty(string filePath)
        {
            string folder = Path.GetDirectoryName(filePath)!;
            string name = Path.GetFileNameWithoutExtension(filePath);

            // Load the font using SharpFont
            using Library lib = new();
            using Face face = new(lib, filePath);

            // Get the list of supported characters
            HashSet<uint> characterSet = GetSupportedCharacters(face);

            // Convert character set to list of strings (to handle code points beyond BMP)
            List<string> characters = [];
            foreach (uint codepoint in characterSet)
            {
                // Exclude control characters and invalid code points
                if (codepoint >= 0x20 && codepoint <= 0x10FFFF)
                    characters.Add(char.ConvertFromUtf32((int)codepoint));
            }
            Characters = characters;
            SKTypeface typeface = SKTypeface.FromFile(filePath);
            GenerateFontAtlas(typeface, characters, Path.Combine(folder, $"{name}.png"), FontDrawSize);
            return true;
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
        public void GenerateFontAtlas(
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
                TextAlign = SKTextAlign.Left,
            };

            using SKFont font = new(typeface, textSize);
            font.BaselineSnap = true;
            font.Edging = SKFontEdging.SubpixelAntialias;
            font.ForceAutoHinting = true;
            font.Subpixel = true;
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

                widths[0] = width;

                SKBitmap bitmap = new(width, height);
                using (SKCanvas canvas = new(bitmap))
                {
                    canvas.Clear(SKColors.Transparent);
                    canvas.DrawText(character, x, y, font, paint);
                }

                glyphBitmaps.Add(bitmap);
                glyphInfos.Add((character, new(
                    new IVector2(width, height),
                    new Vector2(-glyphBounds.Left, -glyphBounds.Top))));
            }

            // Pack glyphs into an atlas
            int glyphsPerRow = (int)Math.Ceiling(Math.Sqrt(glyphBitmaps.Count));
            int maxGlyphWidth = 0;
            int maxGlyphHeight = 0;

            foreach (var g in glyphInfos)
            {
                if (g.info.Size.X > maxGlyphWidth)
                    maxGlyphWidth = g.info.Size.X;

                if (g.info.Size.Y > maxGlyphHeight)
                    maxGlyphHeight = g.info.Size.Y;
            }

            int atlasWidth = maxGlyphWidth * glyphsPerRow;
            int numRows = (int)Math.Ceiling((double)glyphBitmaps.Count / glyphsPerRow);
            int atlasHeight = maxGlyphHeight * numRows;

            using SKBitmap atlasBitmap = new(atlasWidth, atlasHeight);
            using (SKCanvas atlasCanvas = new(atlasBitmap))
            {
                atlasCanvas.Clear(SKColors.Transparent);

                // Draw each glyph onto the atlas
                for (int i = 0; i < glyphBitmaps.Count; i++)
                {
                    int row = i / glyphsPerRow;
                    int col = i % glyphsPerRow;

                    int x = col * maxGlyphWidth;
                    int y = row * maxGlyphHeight;

                    atlasCanvas.DrawBitmap(glyphBitmaps[i], x, y);
                    glyphInfos[i].info.Position = new Vector2(x, y);
                }
            }

            // Save the atlas texture
            using (var image = SKImage.FromBitmap(atlasBitmap))
            using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
            using (var stream = File.OpenWrite(outputAtlasPath))
            {
                data.SaveTo(stream);
            }

            Atlas = new XRTexture2D(outputAtlasPath)
            {
                Resizable = false,
                AutoGenerateMipmaps = true,
                UWrap = ETexWrapMode.MirroredRepeat,
                VWrap = ETexWrapMode.MirroredRepeat,
                MinFilter = ETexMinFilter.LinearMipmapLinear,
                MagFilter = ETexMagFilter.Linear,
                SizedInternalFormat = ESizedInternalFormat.R8,
            };

            Glyphs = glyphInfos.ToDictionary(g => g.character, g => g.info);

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
            float spacing = 5.0f,
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
        /// <exception cref="InvalidOperationException"></exception>
        public void GetQuads(
            string? str,
            List<(Vector4 transform, Vector4 uvs)> quads,
            Vector2 offset,
            float fontSize,
            float spacing = 5.0f,
            float lineSpacing = 5.0f)
        {
            if (Glyphs is null)
                throw new InvalidOperationException("Glyphs are not initialized.");

            if (Atlas is null)
                throw new InvalidOperationException("Atlas is not initialized.");

            GetQuads(
                str,
                Glyphs,
                new IVector2((int)Atlas.Width, (int)Atlas.Height),
                quads,
                offset,
                fontSize,
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
        /// <param name="spacing"></param>
        private static void GetQuads(
            string? str,
            Dictionary<string, Glyph> glyphs,
            IVector2 atlasSize,
            List<(Vector4 transform, Vector4 uvs)> quads,
            Vector2 offset,
            float fontSize,
            float spacing = 5.0f)
        {
            quads.Clear();
            if (str is null)
                return;

            float xOffset = offset.X;
            for (int i = 0; i < str.Length; i++)
            {
                bool last = i == str.Length - 1;
                char ch = str[i];
                string character = ch.ToString();
                if (!glyphs.ContainsKey(character))
                {
                    // Handle missing glyphs (e.g., skip or substitute)
                    continue;
                }

                Glyph glyph = glyphs[character];
                float scale = fontSize / FontDrawSize;
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
                float u1 = (glyph.Position.X + glyph.Size.X) / atlasSize.X;
                float v1 = (glyph.Position.Y + glyph.Size.Y) / atlasSize.Y;

                // Add UVs in the order matching the quad vertices
                // Assuming quad vertices are defined in this order:
                // Bottom-left (0, 0)
                // Bottom-right (1, 0)
                // Top-right (1, 1)
                // Top-left (0, 1)
                Vector4 uvs = new(u0, v0, u1, v1); // Bottom-left to Top-right
                
                quads.Add((transform, uvs));

                xOffset += scaleX;
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
            float spacing = 5.0f,
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
        /// <exception cref="InvalidOperationException"></exception>
        public void GetQuads(
            string? str,
            List<(Vector4 transform, Vector4 uvs)> quads,
            Vector2 offset,
            float? fontSize,
            float maxWidth,
            float maxHeight,
            EWrapMode wrap = EWrapMode.None,
            float spacing = 5.0f,
            float lineSpacing = 5.0f)
        {
            if (Glyphs is null)
                throw new InvalidOperationException("Glyphs are not initialized.");

            if (Atlas is null)
                throw new InvalidOperationException("Atlas is not initialized.");

            GetQuads(
                str,
                Glyphs,
                new IVector2((int)Atlas.Width, (int)Atlas.Height),
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
            List<(Vector4 transform, Vector4 uvs)> quads,
            Vector2 offset,
            float? fontSize,
            float maxWidth,
            float maxHeight,
            EWrapMode wrap = EWrapMode.None,
            float spacing = 5.0f,
            float lineSpacing = 5.0f)
        {
            quads.Clear();
            if (str is null)
                return;

            float xOffset = offset.X;
            float yOffset = offset.Y;
            float lineHeight = fontSize ?? 0.0f;
            float spaceWidth = 30.0f;

            float scale = (fontSize ?? 1.0f) / FontDrawSize;
            for (int i = 0; i < str.Length; i++)
            {
                bool last = i == str.Length - 1;
                bool first = i == 0;

                char ch = str[i];
                string character = ch.ToString();
                if (character == " ")
                {
                    xOffset += spaceWidth;
                    continue;
                }
                if (character == "\n")
                {
                    xOffset = offset.X;
                    //update y translation on all previous characters
                    for (int j = quads.Count - 1; j >= 0; j--)
                    {
                        Vector4 t = quads[j].transform;
                        t.Y += lineHeight + lineSpacing;
                        quads[j] = (t, quads[j].uvs);
                    }
                    //yOffset += lineHeight + 30.0f;
                    //Debug.Out($"Line height: {lineHeight}");
                    if (fontSize is null)
                        lineHeight = 0.0f;
                    continue;
                }
                if (!glyphs.ContainsKey(character))
                {
                    // Handle missing glyphs (e.g., skip or substitute)
                    continue;
                }

                Glyph glyph = glyphs[character];
                float translateX = (xOffset + glyph.Bearing.X) * scale;
                if (first)
                {
                    xOffset -= translateX;
                    translateX = 0.0f;
                }
                float translateY = (yOffset + glyph.Bearing.Y) * scale;
                float scaleX = glyph.Size.X * scale;
                float scaleY = -glyph.Size.Y * scale;

                if (wrap != EWrapMode.None && (translateX + scaleX) > maxWidth && maxWidth > 0.0f)
                {
                    xOffset = offset.X;
                    //update y translation on all previous characters
                    for (int j = quads.Count - 1; j >= 0; j--)
                    {
                        Vector4 t = quads[j].transform;
                        t.Y += lineHeight + lineSpacing;
                        quads[j] = (t, quads[j].uvs);
                    }
                    //yOffset += lineHeight + 30.0f;
                    //Debug.Out($"Line height: {lineHeight}");
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
                float u1 = (glyph.Position.X + glyph.Size.X) / atlasSize.X;
                float v1 = (glyph.Position.Y + glyph.Size.Y) / atlasSize.Y;

                // Add UVs in the order matching the quad vertices
                // Assuming quad vertices are defined in this order:
                // Bottom-left (0, 0)
                // Bottom-right (1, 0)
                // Top-right (1, 1)
                // Top-left (0, 1)
                Vector4 uvs = new(u0, v0, u1, v1); // Bottom-left to Top-right

                quads.Add((transform, uvs));

                xOffset += glyph.Size.X;
                if (!last)
                    xOffset += spacing;

                if (fontSize is null)
                    lineHeight = Math.Max(lineHeight, -scaleY);
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

            return CalculateFontSize(str, Glyphs, offset, bounds, spacing);
        }
        public static float CalculateFontSize(
            string? str,
            Dictionary<string, Glyph> glyphs,
            Vector2 offset,
            Vector2 bounds,
            float spacing = 0.0f)
        {
            if (str is null)
                return 0.0f;

            float maxHeight = 0.0f;
            float xOffset = offset.X;
            for (int i = 0; i < str.Length; i++)
            {
                bool last = i == str.Length - 1;
                char ch = str[i];
                string character = ch.ToString();
                if (!glyphs.ContainsKey(character))
                {
                    // Handle missing glyphs (e.g., skip or substitute)
                    continue;
                }

                Glyph glyph = glyphs[character];
                float scale = 1.0f / FontDrawSize;
                float scaleX = glyph.Size.X * scale;
                float scaleY = -glyph.Size.Y * scale;

                xOffset += glyph.Size.X;
                if (!last)
                    xOffset += spacing;
                maxHeight = Math.Max(maxHeight, scaleY);
            }

            float widthScale = bounds.X / xOffset;
            float heightScale = bounds.Y / maxHeight;
            return Math.Min(widthScale, heightScale);
        }

        public static FontGlyphSet LoadEngineFont(string folderName, string fontName)
            => Engine.Assets.LoadEngineAsset<FontGlyphSet>(
                Engine.Rendering.Constants.EngineFontsCommonFolderName, 
                folderName,
                fontName);

        public static async Task<FontGlyphSet> LoadEngineFontAsync(string folderName, string fontName)
            => await Engine.Assets.LoadEngineAssetAsync<FontGlyphSet>(
                Engine.Rendering.Constants.EngineFontsCommonFolderName,
                folderName,
                fontName);

        public static FontGlyphSet LoadDefaultFont()
            => LoadEngineFont(
                Engine.Rendering.Settings.DefaultFontFolder,
                Engine.Rendering.Settings.DefaultFontFileName);

        public static async Task<FontGlyphSet> LoadDefaultFontAsync()
            => await LoadEngineFontAsync(
                Engine.Rendering.Settings.DefaultFontFolder,
                Engine.Rendering.Settings.DefaultFontFileName);

        public Vector2 MeasureString(string str, float fontSize)
        {
            if (Glyphs is null)
                throw new InvalidOperationException("Glyphs are not initialized.");

            return MeasureString(str, Glyphs, fontSize);
        }

        public static Vector2 MeasureString(string str, Dictionary<string, Glyph> glyphs, float fontSize)
        {
            float width = 0.0f;
            float height = 0.0f;
            float xOffset = 0.0f;
            for (int i = 0; i < str.Length; i++)
            {
                char ch = str[i];
                string character = ch.ToString();
                if (!glyphs.ContainsKey(character))
                {
                    // Handle missing glyphs (e.g., skip or substitute)
                    continue;
                }
                Glyph glyph = glyphs[character];
                float scale = fontSize / FontDrawSize;
                xOffset += glyph.Bearing.X * scale;
                width = Math.Max(width, xOffset + glyph.Size.X * scale);
                height = Math.Max(height, glyph.Size.Y * scale);
                xOffset += glyph.Size.X * scale;
            }
            return new Vector2(width, height);
        }

        public class Glyph
        {
            public Vector2 Position;
            public IVector2 Size;
            public Vector2 Bearing;

            public Glyph() { }
            public Glyph(IVector2 size, Vector2 bearing)
            {
                Size = size;
                Bearing = bearing;
            }
        }
    }
}