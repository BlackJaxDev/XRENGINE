using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;

namespace XREngine.Editor.MaterialAuthoring;

public enum ETextureChannel
{
    Red,
    Green,
    Blue,
    Alpha,
    Luminance,
}

public enum ETextureChannelSourceKind
{
    Image,
    Constant,
    Gradient,
}

public sealed class TexturePackingRecipe
{
    public const int CurrentVersion = 1;
    public int Version { get; init; } = CurrentVersion;
    public int Width { get; init; } = 1024;
    public int Height { get; init; } = 1024;
    public bool LinearData { get; init; } = true;
    public string Filter { get; init; } = "Bilinear";
    public string OutputFormat { get; init; } = "png";
    public int Quality { get; init; } = 95;
    public TexturePackingChannel[] Channels { get; init; } =
    [
        new() { InputChannel = ETextureChannel.Red },
        new() { InputChannel = ETextureChannel.Green },
        new() { InputChannel = ETextureChannel.Blue },
        new() { Kind = ETextureChannelSourceKind.Constant, Constant = 1.0f, InputChannel = ETextureChannel.Alpha },
    ];
    public List<TextureImageOperation> Operations { get; init; } = [];

    public void Validate()
    {
        if (Version != CurrentVersion)
            throw new InvalidDataException($"Unsupported texture recipe version {Version}.");
        if (Width is < 1 or > 16384 || Height is < 1 or > 16384)
            throw new InvalidDataException("Output dimensions must be between 1 and 16384.");
        if (Channels.Length != 4)
            throw new InvalidDataException("Exactly four output channels are required.");
        if (OutputFormat is not ("png" or "jpg" or "jpeg" or "exr"))
            throw new InvalidDataException($"Output format '{OutputFormat}' is unavailable.");
    }

    public string Serialize() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}

public sealed class TexturePackingChannel
{
    public ETextureChannelSourceKind Kind { get; init; }
    public string? SourceAsset { get; init; }
    public ETextureChannel InputChannel { get; init; }
    public float Constant { get; init; }
    public bool Invert { get; init; }
    public Vector2 Remap { get; init; } = new(0.0f, 1.0f);
    public MaterialGradient? Gradient { get; init; }
}

public enum ETextureImageOperationKind
{
    Brightness,
    Hue,
    Saturation,
    Grayscale,
    Rotate,
    Scale,
    Offset,
    Edge,
    Kernel,
    Blend,
}

public sealed record TextureImageOperation(
    ETextureImageOperationKind Kind,
    Vector4 Parameters,
    string? SecondarySource = null);

public readonly record struct TexturePixelSource(int Width, int Height, ReadOnlyMemory<Vector4> Pixels)
{
    public bool IsValid => Width > 0 && Height > 0 && Pixels.Length == Width * Height;
}

public static class MaterialTexturePacker
{
    public static Vector4[] Pack(
        TexturePackingRecipe recipe,
        IReadOnlyDictionary<string, TexturePixelSource> sources,
        CancellationToken cancellationToken = default)
    {
        recipe.Validate();
        Vector4[] output = new Vector4[checked(recipe.Width * recipe.Height)];
        for (int y = 0; y < recipe.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            float v = recipe.Height == 1 ? 0.0f : y / (float)(recipe.Height - 1);
            for (int x = 0; x < recipe.Width; x++)
            {
                float u = recipe.Width == 1 ? 0.0f : x / (float)(recipe.Width - 1);
                float sampleV = v;
                TransformCoordinates(recipe.Operations, ref u, ref sampleV);
                Vector4 packed = default;
                for (int channelIndex = 0; channelIndex < 4; channelIndex++)
                {
                    TexturePackingChannel channel = recipe.Channels[channelIndex];
                    float value = Sample(channel, sources, u, sampleV);
                    value = channel.Invert ? 1.0f - value : value;
                    value = channel.Remap.X + value * (channel.Remap.Y - channel.Remap.X);
                    packed[channelIndex] = Math.Clamp(value, 0.0f, 1.0f);
                }
                output[y * recipe.Width + x] = ApplyOperations(packed, recipe.Operations);
            }
        }
        ApplySpatialOperations(output, recipe.Width, recipe.Height, recipe.Operations, cancellationToken);
        return output;
    }

    public static string ValidateOutputPath(string projectAssetRoot, string outputPath)
    {
        string root = Path.GetFullPath(projectAssetRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string output = Path.GetFullPath(outputPath);
        if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Generated textures must remain inside the approved project asset root.");
        return output;
    }

    public static void SaveRecipe(string outputPath, TexturePackingRecipe recipe)
    {
        string recipePath = $"{outputPath}.xrepack.json";
        string temporary = $"{recipePath}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}.tmp";
        File.WriteAllText(temporary, recipe.Serialize());
        File.Move(temporary, recipePath, true);
    }

    private static float Sample(
        TexturePackingChannel channel,
        IReadOnlyDictionary<string, TexturePixelSource> sources,
        float u,
        float v)
    {
        if (channel.Kind == ETextureChannelSourceKind.Constant)
            return channel.Constant;
        if (channel.Kind == ETextureChannelSourceKind.Gradient)
            return channel.Gradient?.Evaluate(u).X ?? channel.Constant;
        if (channel.SourceAsset is null ||
            !sources.TryGetValue(channel.SourceAsset, out TexturePixelSource source) ||
            !source.IsValid)
            return channel.Constant;

        int x = Math.Clamp((int)MathF.Round(u * (source.Width - 1)), 0, source.Width - 1);
        int y = Math.Clamp((int)MathF.Round(v * (source.Height - 1)), 0, source.Height - 1);
        Vector4 pixel = source.Pixels.Span[y * source.Width + x];
        return channel.InputChannel switch
        {
            ETextureChannel.Red => pixel.X,
            ETextureChannel.Green => pixel.Y,
            ETextureChannel.Blue => pixel.Z,
            ETextureChannel.Alpha => pixel.W,
            _ => Vector3.Dot(new(pixel.X, pixel.Y, pixel.Z), new(0.2126f, 0.7152f, 0.0722f)),
        };
    }

    private static Vector4 ApplyOperations(Vector4 value, IReadOnlyList<TextureImageOperation> operations)
    {
        foreach (TextureImageOperation operation in operations)
        {
            value = operation.Kind switch
            {
                ETextureImageOperationKind.Brightness => new(
                    value.X * operation.Parameters.X,
                    value.Y * operation.Parameters.X,
                    value.Z * operation.Parameters.X,
                    value.W),
                ETextureImageOperationKind.Saturation => ApplySaturation(value, operation.Parameters.X),
                ETextureImageOperationKind.Grayscale => new(
                    Vector3.Dot(new(value.X, value.Y, value.Z), new(0.2126f, 0.7152f, 0.0722f))),
                ETextureImageOperationKind.Hue => ApplyHue(value, operation.Parameters.X),
                ETextureImageOperationKind.Blend => Vector4.Lerp(
                    value,
                    new(operation.Parameters.X, operation.Parameters.Y, operation.Parameters.Z, value.W),
                    Math.Clamp(operation.Parameters.W, 0.0f, 1.0f)),
                _ => value,
            };
        }
        return Vector4.Clamp(value, Vector4.Zero, Vector4.One);
    }

    private static Vector4 ApplySaturation(Vector4 value, float amount)
    {
        float luminance = Vector3.Dot(new(value.X, value.Y, value.Z), new(0.2126f, 0.7152f, 0.0722f));
        return new(
            luminance + (value.X - luminance) * amount,
            luminance + (value.Y - luminance) * amount,
            luminance + (value.Z - luminance) * amount,
            value.W);
    }

    private static Vector4 ApplyHue(Vector4 value, float turns)
    {
        float angle = turns * MathF.Tau;
        float cosine = MathF.Cos(angle);
        float sine = MathF.Sin(angle);
        Vector3 rgb = new(value.X, value.Y, value.Z);
        Vector3 axis = Vector3.Normalize(Vector3.One);
        Vector3 rotated =
            rgb * cosine +
            Vector3.Cross(axis, rgb) * sine +
            axis * Vector3.Dot(axis, rgb) * (1.0f - cosine);
        return new(rotated, value.W);
    }

    private static void TransformCoordinates(
        IReadOnlyList<TextureImageOperation> operations,
        ref float u,
        ref float v)
    {
        Vector2 coordinate = new(u, v);
        bool transformed = false;
        foreach (TextureImageOperation operation in operations)
        {
            switch (operation.Kind)
            {
                case ETextureImageOperationKind.Rotate:
                    transformed = true;
                    float angle = -operation.Parameters.X * MathF.PI / 180.0f;
                    Vector2 centered = coordinate - new Vector2(0.5f);
                    coordinate = new(
                        centered.X * MathF.Cos(angle) - centered.Y * MathF.Sin(angle),
                        centered.X * MathF.Sin(angle) + centered.Y * MathF.Cos(angle));
                    coordinate += new Vector2(0.5f);
                    break;
                case ETextureImageOperationKind.Scale:
                    transformed = true;
                    float scaleX = Math.Max(Math.Abs(operation.Parameters.X), float.Epsilon);
                    float scaleY = Math.Max(Math.Abs(operation.Parameters.Y), float.Epsilon);
                    coordinate = (coordinate - new Vector2(0.5f)) /
                        new Vector2(scaleX, scaleY) + new Vector2(0.5f);
                    break;
                case ETextureImageOperationKind.Offset:
                    transformed = true;
                    coordinate -= new Vector2(operation.Parameters.X, operation.Parameters.Y);
                    break;
            }
        }
        if (!transformed)
            return;
        u = coordinate.X - MathF.Floor(coordinate.X);
        v = coordinate.Y - MathF.Floor(coordinate.Y);
    }

    private static void ApplySpatialOperations(
        Vector4[] pixels,
        int width,
        int height,
        IReadOnlyList<TextureImageOperation> operations,
        CancellationToken cancellationToken)
    {
        foreach (TextureImageOperation operation in operations)
        {
            if (operation.Kind is not (ETextureImageOperationKind.Edge or ETextureImageOperationKind.Kernel))
                continue;

            Vector4[] source = [.. pixels];
            for (int y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int x = 0; x < width; x++)
                {
                    Vector4 accumulated = default;
                    float weightSum = 0.0f;
                    for (int offsetY = -1; offsetY <= 1; offsetY++)
                    {
                        for (int offsetX = -1; offsetX <= 1; offsetX++)
                        {
                            float weight = operation.Kind == ETextureImageOperationKind.Edge
                                ? (offsetX == 0 && offsetY == 0 ? 8.0f : -1.0f)
                                : ResolveKernelWeight(operation.Parameters, offsetX, offsetY);
                            int sampleX = Math.Clamp(x + offsetX, 0, width - 1);
                            int sampleY = Math.Clamp(y + offsetY, 0, height - 1);
                            accumulated += source[sampleY * width + sampleX] * weight;
                            weightSum += weight;
                        }
                    }
                    if (operation.Kind == ETextureImageOperationKind.Kernel && Math.Abs(weightSum) > float.Epsilon)
                        accumulated /= weightSum;
                    accumulated.W = source[y * width + x].W;
                    pixels[y * width + x] = Vector4.Clamp(accumulated, Vector4.Zero, Vector4.One);
                }
            }
        }
    }

    private static float ResolveKernelWeight(Vector4 parameters, int offsetX, int offsetY)
    {
        if (offsetX == 0 && offsetY == 0)
            return parameters.X == 0.0f ? 1.0f : parameters.X;
        if (offsetX == 0 || offsetY == 0)
            return parameters.Y;
        return parameters.Z;
    }
}

public sealed class MaterialGradient
{
    public List<MaterialGradientKey> Keys { get; init; } =
    [
        new(0.0f, Vector4.Zero),
        new(1.0f, Vector4.One),
    ];

    public Vector4 Evaluate(float position)
    {
        if (Keys.Count == 0)
            return Vector4.Zero;
        List<MaterialGradientKey> ordered = [.. Keys.OrderBy(static key => key.Position)];
        position = Math.Clamp(position, 0.0f, 1.0f);
        for (int i = 1; i < ordered.Count; i++)
        {
            if (position > ordered[i].Position)
                continue;
            MaterialGradientKey a = ordered[i - 1];
            MaterialGradientKey b = ordered[i];
            float range = Math.Max(b.Position - a.Position, float.Epsilon);
            return Vector4.Lerp(a.Value, b.Value, (position - a.Position) / range);
        }
        return ordered[^1].Value;
    }

    public Vector4[] Bake(int resolution)
    {
        if (resolution < 1)
            throw new ArgumentOutOfRangeException(nameof(resolution));
        Vector4[] samples = new Vector4[resolution];
        for (int i = 0; i < resolution; i++)
            samples[i] = Evaluate(resolution == 1 ? 0.0f : i / (float)(resolution - 1));
        return samples;
    }
}

public sealed record MaterialGradientKey(float Position, Vector4 Value);

public sealed class MaterialCurve
{
    public List<MaterialCurveKey> Keys { get; init; } = [new(0.0f, 0.0f), new(1.0f, 1.0f)];

    public float Evaluate(float position)
    {
        if (Keys.Count == 0)
            return 0.0f;
        List<MaterialCurveKey> ordered = [.. Keys.OrderBy(static key => key.Position)];
        for (int i = 1; i < ordered.Count; i++)
        {
            if (position > ordered[i].Position)
                continue;
            MaterialCurveKey a = ordered[i - 1];
            MaterialCurveKey b = ordered[i];
            float range = Math.Max(b.Position - a.Position, float.Epsilon);
            float t = Math.Clamp((position - a.Position) / range, 0.0f, 1.0f);
            float t2 = t * t;
            float t3 = t2 * t;
            return (2 * t3 - 3 * t2 + 1) * a.Value +
                   (t3 - 2 * t2 + t) * a.OutTangent * range +
                   (-2 * t3 + 3 * t2) * b.Value +
                   (t3 - t2) * b.InTangent * range;
        }
        return ordered[^1].Value;
    }
}

public sealed record MaterialCurveKey(
    float Position,
    float Value,
    float InTangent = 0.0f,
    float OutTangent = 0.0f);
