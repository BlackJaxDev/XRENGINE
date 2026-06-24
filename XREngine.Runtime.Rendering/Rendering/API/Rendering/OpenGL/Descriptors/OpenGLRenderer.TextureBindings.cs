using XREngine.Extensions;
using ImageMagick;
using ImGuiNET;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ARB;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.OpenGL.Extensions.NV;
using Silk.NET.OpenGL.Extensions.OVR;
using Silk.NET.OpenGLES.Extensions.EXT;
using Silk.NET.OpenGLES.Extensions.NV;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Textures;
using XREngine.Rendering.UI;
using XREngine.Rendering.Shaders.Generator;
using PixelFormat = Silk.NET.OpenGL.PixelFormat;
using XREngine.Components;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    private sealed record BoundTextureDebugState(IGLTexture Texture, string Name, uint BindingId);

    /// <summary>
    /// Tracks the currently active texture unit for accurate per-unit binding optimization.
    /// </summary>
    public int ActiveTextureUnit { get; private set; } = 0;

    private int _maxFragmentTextureImageUnits;
    public int MaxFragmentTextureImageUnits
    {
        get
        {
            if (_maxFragmentTextureImageUnits > 0)
                return _maxFragmentTextureImageUnits;

            int units = Api.GetInteger(GLEnum.MaxTextureImageUnits);
            _maxFragmentTextureImageUnits = units > 0 ? units : 16;
            return _maxFragmentTextureImageUnits;
        }
    }

    /// <summary>
    /// Tracks which texture is bound to each texture unit, keyed by unit index.
    /// This allows proper optimization when the same texture is bound to different units.
    /// </summary>
    private readonly Dictionary<int, IGLTexture?> _boundTexturesPerUnit = new();
    private readonly Dictionary<int, Dictionary<ETextureTarget, BoundTextureDebugState>> _boundTexturesPerUnitTarget = new();
    private string? _currentDrawProgramName;
    private string? _currentDrawMaterialName;
    private string? _currentDrawMeshName;
    private IReadOnlyCollection<int>? _currentDrawTextureUnits;

    /// <summary>
    /// Gets or sets the texture bound to the currently active texture unit.
    /// This property maintains backward compatibility while enabling per-unit tracking.
    /// </summary>
    public IGLTexture? BoundTexture
    {
        get => _boundTexturesPerUnit.TryGetValue(ActiveTextureUnit, out var tex) ? tex : null;
        set => _boundTexturesPerUnit[ActiveTextureUnit] = value;
    }

    public IGLTexture? GetBoundTexture(ETextureTarget target)
    {
        if (_boundTexturesPerUnitTarget.TryGetValue(ActiveTextureUnit, out var unitBindings)
            && unitBindings.TryGetValue(target, out var binding))
        {
            return binding.Texture;
        }

        return null;
    }

    public void SetBoundTexture(ETextureTarget target, IGLTexture? texture, string? name = null)
    {
        BoundTexture = texture;

        if (texture is null)
        {
            if (_boundTexturesPerUnitTarget.TryGetValue(ActiveTextureUnit, out var unitBindings))
            {
                unitBindings.Remove(target);
                if (unitBindings.Count == 0)
                    _boundTexturesPerUnitTarget.Remove(ActiveTextureUnit);
            }

            return;
        }

        if (!_boundTexturesPerUnitTarget.TryGetValue(ActiveTextureUnit, out var trackedBindings))
        {
            trackedBindings = new Dictionary<ETextureTarget, BoundTextureDebugState>();
            _boundTexturesPerUnitTarget[ActiveTextureUnit] = trackedBindings;
        }

        trackedBindings[target] = new BoundTextureDebugState(
            texture,
            string.IsNullOrWhiteSpace(name) ? texture.GetType().Name : name!,
            texture.BindingId);
    }

    /// <summary>
    /// Unbinds any texture targets on the currently active unit that differ from <paramref name="keepTarget"/>.
    /// Prevents NVIDIA "program texture usage" errors caused by stale bindings on the same unit
    /// (e.g. a cubemap left over from a previous pass when the shader expects sampler2D).
    /// </summary>
    public void ClearConflictingTextureTargets(ETextureTarget keepTarget)
    {
        if (!_boundTexturesPerUnitTarget.TryGetValue(ActiveTextureUnit, out var unitBindings))
            return;

        // Collect targets to remove (can't modify dictionary while iterating).
        List<ETextureTarget>? toRemove = null;
        foreach (var kvp in unitBindings)
        {
            if (kvp.Key == keepTarget)
                continue;

            toRemove ??= [];
            toRemove.Add(kvp.Key);
        }

        if (toRemove is null)
            return;

        foreach (var staleTarget in toRemove)
        {
            Api.BindTexture(GLObjectBase.ToGLEnum(staleTarget), 0);
            unitBindings.Remove(staleTarget);
        }

        if (unitBindings.Count == 0)
            _boundTexturesPerUnitTarget.Remove(ActiveTextureUnit);
    }

    public void SetDrawDebugContext(string? programName, string? materialName, string? meshName, IReadOnlyCollection<int>? textureUnits)
    {
        _currentDrawProgramName = string.IsNullOrWhiteSpace(programName) ? null : programName;
        _currentDrawMaterialName = string.IsNullOrWhiteSpace(materialName) ? null : materialName;
        _currentDrawMeshName = string.IsNullOrWhiteSpace(meshName) ? null : meshName;
        _currentDrawTextureUnits = textureUnits is { Count: > 0 } ? textureUnits : null;
    }

    public void ClearDrawDebugContext()
    {
        _currentDrawProgramName = null;
        _currentDrawMaterialName = null;
        _currentDrawMeshName = null;
        _currentDrawTextureUnits = null;
    }

    private string BuildOpenGLErrorContext()
    {
        StringBuilder sb = new();

        if (!string.IsNullOrWhiteSpace(_currentDrawProgramName)
            || !string.IsNullOrWhiteSpace(_currentDrawMaterialName)
            || !string.IsNullOrWhiteSpace(_currentDrawMeshName))
        {
            sb.Append("[GL Error Context] DrawProgram='")
                .Append(_currentDrawProgramName ?? "<unknown>")
                .Append("', Material='")
                .Append(_currentDrawMaterialName ?? "<unknown>")
                .Append("', Mesh='")
                .Append(_currentDrawMeshName ?? "<unknown>")
                .AppendLine("'");
        }

        int currentProgramId = Api.GetInteger(GetPName.CurrentProgram);
        sb.Append("[GL Error Context] CurrentProgramId=")
            .Append(currentProgramId)
            .Append(", ActiveTextureUnit=")
            .Append(ActiveTextureUnit)
            .Append(", MaxFragmentTextureUnits=")
            .Append(MaxFragmentTextureImageUnits)
            .AppendLine();

        sb.Append("[GL Error Context] TextureUnits=");
        AppendTrackedTextureUnits(sb);
        return sb.ToString().TrimEnd();
    }

    private void AppendTrackedTextureUnits(StringBuilder sb)
    {
        int[] units;
        if (_currentDrawTextureUnits is ICollection<int> currentTextureUnits && currentTextureUnits.Count > 0)
        {
            units = new int[currentTextureUnits.Count];
            currentTextureUnits.CopyTo(units, 0);
        }
        else if (_currentDrawTextureUnits is { Count: > 0 } fallbackTextureUnits)
        {
            units = new int[fallbackTextureUnits.Count];
            int index = 0;
            foreach (int unit in fallbackTextureUnits)
                units[index++] = unit;
        }
        else
        {
            units = [.. _boundTexturesPerUnitTarget.Keys];
        }

        if (units.Length == 0)
        {
            sb.Append("<none>");
            return;
        }

        Array.Sort(units);
        bool wroteAnyUnit = false;
        foreach (int unit in units)
        {
            if (!_boundTexturesPerUnitTarget.TryGetValue(unit, out var bindings) || bindings.Count == 0)
                continue;

            if (wroteAnyUnit)
                sb.Append("; ");

            sb.Append("unit ").Append(unit).Append("=[");
            bool wroteBinding = false;
            foreach (var pair in bindings)
            {
                if (wroteBinding)
                    sb.Append(", ");

                sb.Append(pair.Key)
                    .Append(':')
                    .Append(pair.Value.Name)
                    .Append('#')
                    .Append(pair.Value.BindingId);
                wroteBinding = true;
            }
            sb.Append(']');
            wroteAnyUnit = true;
        }

        if (!wroteAnyUnit)
            sb.Append("<none>");
    }

    /// <summary>
    /// Sets the active texture unit and updates internal tracking.
    /// Should be called instead of directly calling Api.ActiveTexture.
    /// </summary>
    /// <param name="unit">The texture unit to activate (0-based index).</param>
    public void SetActiveTextureUnit(int unit)
    {
        if (ActiveTextureUnit == unit)
            RuntimeEngine.Rendering.Stats.RecordRendererStateCounter(ERendererProfilerCounter.RedundantStateSkips);
        else
            RuntimeEngine.Rendering.Stats.RecordRendererStateCounter(ERendererProfilerCounter.TextureUnitSwitches);
        ActiveTextureUnit = unit;
        Api.ActiveTexture(GLEnum.Texture0 + unit);
    }
}
