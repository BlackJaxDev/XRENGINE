using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Shaders.Parameters;

namespace XREngine.Rendering.UI;

/// <summary>Copies bounded numeric and raster state on the canvas owner.</summary>
internal static class UIDrawStateCapture
{
    internal const int MaximumArrayLength = 65536;

    internal static ShaderVar CloneParameter(ShaderVar source, int depth = 0)
    {
        if (depth > 8)
            throw new NotSupportedException("UI parameter nesting exceeds the capture limit.");
        ShaderVar copy;
        if (source is ShaderArrayBase array)
        {
            if (array.Length > MaximumArrayLength)
                throw new NotSupportedException("UI parameter array exceeds the capture limit.");
            copy = array.CloneValues(child => CloneParameter(child, depth + 1));
        }
        else
            copy = source switch
            {
                ShaderBool value => new ShaderBool(value.Value, value.Name),
                ShaderInt value => new ShaderInt(value.Value, value.Name),
                ShaderUInt value => new ShaderUInt(value.Value, value.Name),
                ShaderFloat value => new ShaderFloat(value.Value, value.Name),
                ShaderDouble value => new ShaderDouble(value.Value, value.Name),
                ShaderVector2 value => new ShaderVector2(value.Value, value.Name),
                ShaderVector3 value => new ShaderVector3(value.Value, value.Name),
                ShaderVector4 value => new ShaderVector4(value.Value, value.Name),
                ShaderBVector2 value => new ShaderBVector2(value.Value, value.Name),
                ShaderBVector3 value => new ShaderBVector3(value.Value, value.Name),
                ShaderBVector4 value => new ShaderBVector4(value.Value, value.Name),
                ShaderIVector2 value => new ShaderIVector2(value.Value, value.Name),
                ShaderIVector3 value => new ShaderIVector3(value.Value, value.Name),
                ShaderIVector4 value => new ShaderIVector4(value.Value, value.Name),
                ShaderUVector2 value => new ShaderUVector2(value.Value, value.Name),
                ShaderUVector3 value => new ShaderUVector3(value.Value, value.Name),
                ShaderUVector4 value => new ShaderUVector4(value.Value, value.Name),
                ShaderDVector2 value => new ShaderDVector2(value.Value, value.Name),
                ShaderDVector3 value => new ShaderDVector3(value.Value, value.Name),
                ShaderDVector4 value => new ShaderDVector4(value.Value, value.Name),
                ShaderMat4 value => new ShaderMat4(value.Value, value.Name),
                _ => throw new NotSupportedException($"UI parameter '{source.Name}' requires a bounded value capture adapter."),
            };
        if (copy.GetType() != source.GetType())
            throw new NotSupportedException($"UI parameter '{source.Name}' requires a bounded value capture adapter.");
        return copy;
    }

    internal static bool ParameterEquals(ShaderVar left, ShaderVar right, int depth = 0)
    {
        if (depth > 8 || left.GetType() != right.GetType() || left.Name != right.Name)
            return false;
        if (left is ShaderArrayBase array && right is ShaderArrayBase other)
        {
            ShaderVar[]? a = array.CaptureValues;
            ShaderVar[]? b = other.CaptureValues;
            if (a is null || b is null)
                return a is null && b is null;
            if (a.Length != b.Length || a.Length > MaximumArrayLength)
                return false;
            for (int index = 0; index < a.Length; index++)
            {
                if (a[index] is { } x && b[index] is { } y)
                {
                    if (!ParameterEquals(x, y, depth + 1))
                        return false;
                }
                else if (a[index] is not null || b[index] is not null)
                    return false;
            }
            return true;
        }
        return (left, right) switch
        {
            (ShaderBool a, ShaderBool b) => a.Value.Equals(b.Value),
            (ShaderInt a, ShaderInt b) => a.Value.Equals(b.Value),
            (ShaderUInt a, ShaderUInt b) => a.Value.Equals(b.Value),
            (ShaderFloat a, ShaderFloat b) => a.Value.Equals(b.Value),
            (ShaderDouble a, ShaderDouble b) => a.Value.Equals(b.Value),
            (ShaderVector2 a, ShaderVector2 b) => a.Value.Equals(b.Value),
            (ShaderVector3 a, ShaderVector3 b) => a.Value.Equals(b.Value),
            (ShaderVector4 a, ShaderVector4 b) => a.Value.Equals(b.Value),
            (ShaderBVector2 a, ShaderBVector2 b) => a.Value.X.Equals(b.Value.X) && a.Value.Y.Equals(b.Value.Y),
            (ShaderBVector3 a, ShaderBVector3 b) => a.Value.X.Equals(b.Value.X) && a.Value.Y.Equals(b.Value.Y) && a.Value.Z.Equals(b.Value.Z),
            (ShaderBVector4 a, ShaderBVector4 b) => a.Value.X.Equals(b.Value.X) && a.Value.Y.Equals(b.Value.Y) && a.Value.Z.Equals(b.Value.Z) && a.Value.W.Equals(b.Value.W),
            (ShaderIVector2 a, ShaderIVector2 b) => a.Value.X.Equals(b.Value.X) && a.Value.Y.Equals(b.Value.Y),
            (ShaderIVector3 a, ShaderIVector3 b) => a.Value.X.Equals(b.Value.X) && a.Value.Y.Equals(b.Value.Y) && a.Value.Z.Equals(b.Value.Z),
            (ShaderIVector4 a, ShaderIVector4 b) => a.Value.X.Equals(b.Value.X) && a.Value.Y.Equals(b.Value.Y) && a.Value.Z.Equals(b.Value.Z) && a.Value.W.Equals(b.Value.W),
            (ShaderUVector2 a, ShaderUVector2 b) => a.Value.X.Equals(b.Value.X) && a.Value.Y.Equals(b.Value.Y),
            (ShaderUVector3 a, ShaderUVector3 b) => a.Value.X.Equals(b.Value.X) && a.Value.Y.Equals(b.Value.Y) && a.Value.Z.Equals(b.Value.Z),
            (ShaderUVector4 a, ShaderUVector4 b) => a.Value.X.Equals(b.Value.X) && a.Value.Y.Equals(b.Value.Y) && a.Value.Z.Equals(b.Value.Z) && a.Value.W.Equals(b.Value.W),
            (ShaderDVector2 a, ShaderDVector2 b) => a.Value.X.Equals(b.Value.X) && a.Value.Y.Equals(b.Value.Y),
            (ShaderDVector3 a, ShaderDVector3 b) => a.Value.X.Equals(b.Value.X) && a.Value.Y.Equals(b.Value.Y) && a.Value.Z.Equals(b.Value.Z),
            (ShaderDVector4 a, ShaderDVector4 b) => a.Value.X.Equals(b.Value.X) && a.Value.Y.Equals(b.Value.Y) && a.Value.Z.Equals(b.Value.Z) && a.Value.W.Equals(b.Value.W),
            (ShaderMat4 a, ShaderMat4 b) => a.Value.Equals(b.Value),
            _ => false,
        };
    }

    internal static T CloneRaster<T>(T source) where T : class
    {
        object copy = source switch
        {
            RenderingParameters value => new RenderingParameters
            {
                RequiredEngineUniforms = value.RequiredEngineUniforms,
                WriteRed = value.WriteRed, WriteGreen = value.WriteGreen,
                WriteBlue = value.WriteBlue, WriteAlpha = value.WriteAlpha,
                AlphaToCoverage = value.AlphaToCoverage, Winding = value.Winding, CullMode = value.CullMode,
                DepthTest = CloneRaster(value.DepthTest), StencilTest = CloneRaster(value.StencilTest),
                BlendModesPerDrawBuffer = CloneModes(value.BlendModesPerDrawBuffer),
                BlendModeAllDrawBuffers = value.BlendModeAllDrawBuffers is { } blend ? CloneRaster(blend) : null,
                ExcludeFromGpuIndirect = value.ExcludeFromGpuIndirect, ExcludeFromCpuOcclusion = value.ExcludeFromCpuOcclusion,
                TextureArrayPolicy = value.TextureArrayPolicy, MissingTextureFallback = value.MissingTextureFallback,
            },
            DepthTest value => new DepthTest { Enabled = value.Enabled, UpdateDepth = value.UpdateDepth, Function = value.Function },
            StencilTest value => new StencilTest
            {
                Enabled = value.Enabled, FrontFace = CloneRaster(value.FrontFace), BackFace = CloneRaster(value.BackFace),
            },
            StencilTestFace value => new StencilTestFace
            {
                BothFailOp = value.BothFailOp, StencilPassDepthFailOp = value.StencilPassDepthFailOp, BothPassOp = value.BothPassOp,
                Function = value.Function, Reference = value.Reference, ReadMask = value.ReadMask, WriteMask = value.WriteMask,
            },
            BlendMode value => new BlendMode
            {
                Enabled = value.Enabled, RgbEquation = value.RgbEquation, AlphaEquation = value.AlphaEquation,
                RgbSrcFactor = value.RgbSrcFactor, AlphaSrcFactor = value.AlphaSrcFactor,
                RgbDstFactor = value.RgbDstFactor, AlphaDstFactor = value.AlphaDstFactor,
            },
            _ => throw new NotSupportedException("UI raster state requires a typed capture adapter."),
        };
        if (copy.GetType() != source.GetType())
            throw new NotSupportedException("UI raster state requires a typed capture adapter.");
        return (T)copy;
    }

    private static Dictionary<uint, BlendMode>? CloneModes(Dictionary<uint, BlendMode>? modes)
    {
        if (modes is null)
            return null;
        if (modes.Count > 32)
            throw new NotSupportedException("UI blend attachment count exceeds the capture limit.");
        Dictionary<uint, BlendMode> copy = new(modes.Count);
        foreach ((uint key, BlendMode value) in modes)
            copy.Add(key, CloneRaster(value));
        return copy;
    }

    internal static bool RasterEquals(object? left, object? right)
    {
        if (left is null || right is null)
            return left is null && right is null;
        if (left.GetType() != right.GetType())
            return false;
        return (left, right) switch
        {
            (RenderingParameters a, RenderingParameters b) =>
                a.RequiredEngineUniforms == b.RequiredEngineUniforms && a.WriteRed == b.WriteRed && a.WriteGreen == b.WriteGreen &&
                a.WriteBlue == b.WriteBlue && a.WriteAlpha == b.WriteAlpha && a.AlphaToCoverage == b.AlphaToCoverage &&
                a.Winding == b.Winding && a.CullMode == b.CullMode && RasterEquals(a.DepthTest, b.DepthTest) &&
                RasterEquals(a.StencilTest, b.StencilTest) && ModesEqual(a.BlendModesPerDrawBuffer, b.BlendModesPerDrawBuffer) &&
                RasterEquals(a.BlendModeAllDrawBuffers, b.BlendModeAllDrawBuffers) &&
                a.ExcludeFromGpuIndirect == b.ExcludeFromGpuIndirect && a.ExcludeFromCpuOcclusion == b.ExcludeFromCpuOcclusion &&
                a.TextureArrayPolicy == b.TextureArrayPolicy && a.MissingTextureFallback == b.MissingTextureFallback,
            (DepthTest a, DepthTest b) => a.Enabled == b.Enabled && a.UpdateDepth == b.UpdateDepth && a.Function == b.Function,
            (StencilTest a, StencilTest b) => a.Enabled == b.Enabled && RasterEquals(a.FrontFace, b.FrontFace) && RasterEquals(a.BackFace, b.BackFace),
            (StencilTestFace a, StencilTestFace b) => a.BothFailOp == b.BothFailOp && a.StencilPassDepthFailOp == b.StencilPassDepthFailOp &&
                a.BothPassOp == b.BothPassOp && a.Function == b.Function && a.Reference == b.Reference && a.ReadMask == b.ReadMask && a.WriteMask == b.WriteMask,
            (BlendMode a, BlendMode b) => a.Enabled == b.Enabled && a.RgbEquation == b.RgbEquation && a.AlphaEquation == b.AlphaEquation &&
                a.RgbSrcFactor == b.RgbSrcFactor && a.AlphaSrcFactor == b.AlphaSrcFactor && a.RgbDstFactor == b.RgbDstFactor && a.AlphaDstFactor == b.AlphaDstFactor,
            _ => false,
        };
    }

    private static bool ModesEqual(Dictionary<uint, BlendMode>? left, Dictionary<uint, BlendMode>? right)
    {
        if (left is null || right is null)
            return left is null && right is null;
        if (left.Count != right.Count || left.Count > 32)
            return false;
        foreach ((uint key, BlendMode mode) in left)
            if (!right.TryGetValue(key, out BlendMode? other) || !RasterEquals(mode, other))
                return false;
        return true;
    }
}
