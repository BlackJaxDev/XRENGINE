using System.Collections.Concurrent;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using ImageMagick;
using XREngine.Rendering;

namespace XREngine.Editor.MaterialAuthoring;

public enum EMaterialTextureColorSpace
{
    Linear,
    Srgb,
}

public enum EMaterialTextureResampling
{
    Nearest,
    Bilinear,
    Bicubic,
}

public sealed record MaterialTextureOutputPolicy(
    int Width,
    int Height,
    EMaterialTextureResampling Resampling,
    EMaterialTextureColorSpace ColorSpace,
    bool AlphaIsTransparency,
    bool GenerateMipmaps,
    string Compression,
    int Quality)
{
    public void Validate()
    {
        if (Width is < 1 or > 16384 || Height is < 1 or > 16384)
            throw new InvalidDataException("Texture dimensions must be between 1 and 16384.");
        if (Quality is < 1 or > 100)
            throw new InvalidDataException("Texture quality must be between 1 and 100.");
        if (string.IsNullOrWhiteSpace(Compression))
            throw new InvalidDataException("An explicit compression policy is required.");
    }
}

public sealed record MaterialTextureDependency(
    string AssetPath,
    long Length,
    DateTime LastWriteUtc,
    string Sha256)
{
    public static MaterialTextureDependency Capture(string assetPath)
    {
        FileInfo file = new(assetPath);
        if (!file.Exists)
            throw new FileNotFoundException("Texture source is missing.", assetPath);
        using FileStream stream = file.OpenRead();
        return new(
            file.FullName,
            file.Length,
            file.LastWriteTimeUtc,
            Convert.ToHexString(SHA256.HashData(stream)));
    }

    public bool IsCurrent()
    {
        FileInfo file = new(AssetPath);
        return file.Exists &&
               file.Length == Length &&
               file.LastWriteTimeUtc == LastWriteUtc;
    }
}

public sealed record MaterialTextureBuildManifest(
    int Version,
    string OutputPath,
    MaterialTextureOutputPolicy Policy,
    IReadOnlyList<MaterialTextureDependency> Dependencies)
{
    public const int CurrentVersion = 1;

    public IReadOnlyList<string> GetStaleOrMissingInputs()
    {
        List<string> stale = [];
        foreach (MaterialTextureDependency dependency in Dependencies)
            if (!dependency.IsCurrent())
                stale.Add(dependency.AssetPath);
        return stale;
    }
}

/// <summary>
/// Deterministic encoder and sidecar writer for authoring-generated textures.
/// Encoding happens off-thread; the caller owns editor-thread import/assignment.
/// </summary>
public static class MaterialTextureAssetWriter
{
    public static async Task<MaterialTextureBuildManifest> WriteAsync(
        string projectAssetRoot,
        string outputPath,
        ReadOnlyMemory<Vector4> pixels,
        MaterialTextureOutputPolicy policy,
        IReadOnlyList<string> sourcePaths,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        policy.Validate();
        string validatedOutput = MaterialTexturePacker.ValidateOutputPath(projectAssetRoot, outputPath);
        string extension = Path.GetExtension(validatedOutput).ToLowerInvariant();
        MagickFormat format = extension switch
        {
            ".png" => MagickFormat.Png,
            ".jpg" or ".jpeg" => MagickFormat.Jpeg,
            ".exr" => MagickFormat.Exr,
            _ => throw new InvalidDataException($"Encoding '{extension}' is unavailable."),
        };
        if (pixels.Length != checked(policy.Width * policy.Height))
            throw new InvalidDataException("Pixel count does not match the output policy.");
        if (File.Exists(validatedOutput) && !overwrite)
            throw new IOException("The output already exists and overwrite was not confirmed.");

        MaterialTextureDependency[] dependencies = new MaterialTextureDependency[sourcePaths.Count];
        for (int i = 0; i < sourcePaths.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            dependencies[i] = MaterialTextureDependency.Capture(sourcePaths[i]);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(validatedOutput)!);
        string temporary = $"{validatedOutput}.{Guid.NewGuid():N}.tmp";
        try
        {
            await Task.Run(
                () => Encode(temporary, pixels.Span, policy, format, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, validatedOutput, overwrite);

            MaterialTextureBuildManifest manifest = new(
                MaterialTextureBuildManifest.CurrentVersion,
                validatedOutput,
                policy,
                dependencies);
            string sidecar = $"{validatedOutput}.xretexture.json";
            File.WriteAllText(
                sidecar,
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            return manifest;
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static void Encode(
        string outputPath,
        ReadOnlySpan<Vector4> pixels,
        MaterialTextureOutputPolicy policy,
        MagickFormat format,
        CancellationToken cancellationToken)
    {
        float[] rgba = new float[pixels.Length * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            if ((i & 0x3fff) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            Vector4 pixel = Vector4.Clamp(pixels[i], Vector4.Zero, Vector4.One);
            int offset = i * 4;
            rgba[offset] = pixel.X;
            rgba[offset + 1] = pixel.Y;
            rgba[offset + 2] = pixel.Z;
            rgba[offset + 3] = pixel.W;
        }

        using MagickImage image = new(MagickColors.Transparent, (uint)policy.Width, (uint)policy.Height);
        image.ImportPixels(
            rgba,
            new PixelImportSettings(
                (uint)policy.Width,
                (uint)policy.Height,
                StorageType.Float,
                PixelMapping.RGBA));
        image.ColorSpace = policy.ColorSpace == EMaterialTextureColorSpace.Srgb
            ? ColorSpace.sRGB
            : ColorSpace.RGB;
        image.Quality = (uint)policy.Quality;
        image.Write(outputPath, format);
    }
}

public enum EMaterialGradientInterpolation
{
    Linear,
    Smooth,
    Constant,
}

public sealed class MaterialGradientAsset
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public EMaterialGradientInterpolation Interpolation { get; set; }
    public bool Vertical { get; set; }
    public EMaterialTextureColorSpace ColorSpace { get; set; } = EMaterialTextureColorSpace.Srgb;
    public int Resolution { get; set; } = 256;
    public List<MaterialGradientKey> ColorKeys { get; init; } =
        [new(0.0f, Vector4.Zero), new(1.0f, Vector4.One)];
    public List<MaterialGradientKey> AlphaKeys { get; init; } =
        [new(0.0f, Vector4.One), new(1.0f, Vector4.One)];

    public Vector4 Evaluate(float position)
    {
        Vector4 color = EvaluateKeys(ColorKeys, position);
        Vector4 alpha = EvaluateKeys(AlphaKeys, position);
        color.W = alpha.X;
        return color;
    }

    public Vector4[] Bake()
    {
        if (Resolution is < 2 or > 16384)
            throw new InvalidDataException("Gradient resolution must be between 2 and 16384.");
        Vector4[] result = new Vector4[Resolution];
        for (int i = 0; i < result.Length; i++)
            result[i] = Evaluate(i / (float)(result.Length - 1));
        return result;
    }

    public void Normalize()
    {
        NormalizeKeys(ColorKeys);
        NormalizeKeys(AlphaKeys);
    }

    private Vector4 EvaluateKeys(List<MaterialGradientKey> keys, float position)
    {
        if (keys.Count == 0)
            return Vector4.Zero;
        position = Math.Clamp(position, 0.0f, 1.0f);
        for (int i = 1; i < keys.Count; i++)
        {
            if (position > keys[i].Position)
                continue;
            MaterialGradientKey left = keys[i - 1];
            MaterialGradientKey right = keys[i];
            float range = Math.Max(right.Position - left.Position, float.Epsilon);
            float t = Math.Clamp((position - left.Position) / range, 0.0f, 1.0f);
            t = Interpolation switch
            {
                EMaterialGradientInterpolation.Constant => 0.0f,
                EMaterialGradientInterpolation.Smooth => t * t * (3.0f - 2.0f * t),
                _ => t,
            };
            return Vector4.Lerp(left.Value, right.Value, t);
        }
        return keys[^1].Value;
    }

    private static void NormalizeKeys(List<MaterialGradientKey> keys)
    {
        keys.Sort(static (left, right) => left.Position.CompareTo(right.Position));
        for (int i = 0; i < keys.Count; i++)
            keys[i] = keys[i] with { Position = Math.Clamp(keys[i].Position, 0.0f, 1.0f) };
    }
}

public sealed class MaterialCurveAsset
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public Vector2 InputRange { get; set; } = new(0.0f, 1.0f);
    public Vector2 OutputRange { get; set; } = new(0.0f, 1.0f);
    public MaterialCurve[] Channels { get; init; } =
        [new(), new(), new(), new()];
    public int Resolution { get; set; } = 256;

    public Vector4[] Bake()
    {
        if (Channels.Length != 4)
            throw new InvalidDataException("A curve asset requires four channels.");
        if (Resolution is < 2 or > 16384)
            throw new InvalidDataException("Curve resolution must be between 2 and 16384.");
        Vector4[] values = new Vector4[Resolution];
        for (int i = 0; i < values.Length; i++)
        {
            float t = i / (float)(values.Length - 1);
            values[i] = new(
                Channels[0].Evaluate(t),
                Channels[1].Evaluate(t),
                Channels[2].Evaluate(t),
                Channels[3].Evaluate(t));
        }
        return values;
    }
}

public sealed class MaterialRamp4
{
    public Vector4 Positions { get; private set; } = new(0.0f, 0.333333f, 0.666667f, 1.0f);
    public Vector4[] Values { get; } = [Vector4.Zero, Vector4.One, Vector4.One, Vector4.One];

    public void SetStop(int index, float position, Vector4 value)
    {
        if ((uint)index >= 4)
            throw new ArgumentOutOfRangeException(nameof(index));
        Vector4 positions = Positions;
        positions[index] = Math.Clamp(position, 0.0f, 1.0f);
        Positions = positions;
        Values[index] = value;
        StableSort();
    }

    private void StableSort()
    {
        Span<(float Position, Vector4 Value, int Order)> stops =
        [
            (Positions.X, Values[0], 0),
            (Positions.Y, Values[1], 1),
            (Positions.Z, Values[2], 2),
            (Positions.W, Values[3], 3),
        ];
        stops.Sort(static (left, right) =>
        {
            int position = left.Position.CompareTo(right.Position);
            return position != 0 ? position : left.Order.CompareTo(right.Order);
        });
        for (int i = 0; i < stops.Length; i++)
        {
            Vector4 positions = Positions;
            positions[i] = stops[i].Position;
            Positions = positions;
            Values[i] = stops[i].Value;
        }
    }
}

public sealed record MaterialTextureArrayLayer(
    string SourcePath,
    int Width,
    int Height,
    string Format,
    int MipCount,
    EMaterialTextureColorSpace ColorSpace,
    string Semantic);

public sealed class MaterialTextureArrayRecipe
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public List<MaterialTextureArrayLayer> Layers { get; init; } = [];
    public bool AllowResample { get; set; }
    public EMaterialTextureResampling Resampling { get; set; } = EMaterialTextureResampling.Bilinear;

    public IReadOnlyList<string> Validate()
    {
        List<string> diagnostics = [];
        if (Layers.Count == 0)
        {
            diagnostics.Add("At least one array layer is required.");
            return diagnostics;
        }

        MaterialTextureArrayLayer first = Layers[0];
        for (int i = 1; i < Layers.Count; i++)
        {
            MaterialTextureArrayLayer layer = Layers[i];
            if (!AllowResample && (layer.Width != first.Width || layer.Height != first.Height))
                diagnostics.Add($"Layer {i} dimensions differ and resampling is disabled.");
            if (!string.Equals(layer.Format, first.Format, StringComparison.OrdinalIgnoreCase))
                diagnostics.Add($"Layer {i} format differs from layer 0.");
            if (layer.MipCount != first.MipCount)
                diagnostics.Add($"Layer {i} mip count differs from layer 0.");
            if (layer.ColorSpace != first.ColorSpace)
                diagnostics.Add($"Layer {i} color space differs from layer 0.");
            if (!string.Equals(layer.Semantic, first.Semantic, StringComparison.Ordinal))
                diagnostics.Add($"Layer {i} semantic differs from layer 0.");
        }
        return diagnostics;
    }

    public void Move(int from, int to)
    {
        if ((uint)from >= Layers.Count || (uint)to >= Layers.Count)
            throw new ArgumentOutOfRangeException();
        MaterialTextureArrayLayer layer = Layers[from];
        Layers.RemoveAt(from);
        Layers.Insert(to, layer);
    }
}

/// <summary>
/// Bounded cancellable job registry for texture previews, encoders, and variant
/// preparation. Completed jobs are evicted in insertion order.
/// </summary>
public sealed class MaterialAuthoringJobManager : IDisposable
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _jobs = new();
    private readonly int _maximumJobs;
    private bool _disposed;

    public MaterialAuthoringJobManager(int maximumJobs = 16)
        => _maximumJobs = Math.Clamp(maximumJobs, 1, 128);

    public int ActiveCount => _jobs.Count;

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (_jobs.Count >= _maximumJobs)
            throw new InvalidOperationException("The authoring job budget is exhausted.");

        Guid id = Guid.NewGuid();
        CancellationTokenSource cancellation = new();
        if (!_jobs.TryAdd(id, cancellation))
            throw new InvalidOperationException("Unable to register the authoring job.");
        try
        {
            return await operation(cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            _jobs.TryRemove(id, out _);
            cancellation.Dispose();
        }
    }

    public void CancelAll()
    {
        foreach (CancellationTokenSource cancellation in _jobs.Values)
            cancellation.Cancel();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CancelAll();
    }
}
