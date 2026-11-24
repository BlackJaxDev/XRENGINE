using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Commands;

public sealed partial class AdvancedGpuScenePublisher
{
    /// <summary>The flat base color of the canonical error material (magenta).</summary>
    private static readonly ColorF4 ErrorMaterialColor = new(1.0f, 0.0f, 1.0f, 1.0f);

    /// <summary>The minimum time between two error material reports of one publisher.</summary>
    private static readonly long ErrorMaterialReportRepeatTicks = System.Diagnostics.Stopwatch.Frequency * 5;

    /// <summary>
    /// The shared canonical error material. It is a standard deferred color material without
    /// textures, so the standard material encoder always accepts it. The publisher creates
    /// the material once, on first use.
    /// </summary>
    private static readonly Lazy<XRMaterial> s_errorMaterial =
        new(CreateErrorMaterial, LazyThreadSafetyMode.ExecutionAndPublication);

    private int _errorMaterialRowCount;
    private EAdvancedCanonicalCompatibilityReason _firstErrorMaterialReason;
    private string? _firstErrorMaterialText;
    private XRMaterial? _firstErrorMaterialSource;
    private XRMesh? _firstErrorMaterialMesh;
    private long _errorMaterialReportTimestamp;

    /// <summary>
    /// Gets the number of rows in the last whole-scene plan that draw the canonical error
    /// material because the canonical scene cannot encode their own material.
    /// </summary>
    public int LastErrorMaterialRowCount => _errorMaterialRowCount;

    private static XRMaterial CreateErrorMaterial()
    {
        XRMaterial material = XRMaterial.CreateColorMaterialDeferred(ErrorMaterialColor);
        material.Name = "AdvancedCanonicalErrorMaterial";
        return material;
    }

    /// <summary>Clears the error material record at the start of a whole-scene plan.</summary>
    private void ResetErrorMaterialRows()
    {
        _errorMaterialRowCount = 0;
        _firstErrorMaterialReason = EAdvancedCanonicalCompatibilityReason.None;
        _firstErrorMaterialText = null;
        _firstErrorMaterialSource = null;
        _firstErrorMaterialMesh = null;
    }

    /// <summary>
    /// Plans a row with the canonical error material after the canonical scene rejected the
    /// material of the row. Without a canonical row, the Advanced pipeline rejects every
    /// frame package in which the draw is an enabled primary draw, so one unsupported
    /// material would stop all scene output. The error row draws the geometry in a flat
    /// magenta color instead, and <see cref="ReportErrorMaterialRows"/> names the cause.
    /// </summary>
    /// <remarks>
    /// The row keeps the command, geometry, bounds and render options of the source. The
    /// registration stores the error material, so the unchanged-row check sees a different
    /// material on each later plan, so each publication plans the whole scene while an error
    /// row exists. The plan tries the source material first, so a material that becomes
    /// supported replaces the error material on the next plan. If the error material also fails, the plan values are restored and the
    /// row stays unsupported.
    /// </remarks>
    private bool TrySubstituteErrorMaterial(
        ref AdvancedGpuSceneCommandTransition plan,
        in DrawMetadata command,
        int registrationIndex,
        EAdvancedCanonicalCompatibilityReason sourceReason,
        string sourceReasonText,
        out int materialPlanIndex,
        out bool fatal,
        out string reason)
    {
        XRMaterial error = s_errorMaterial.Value;
        XRMaterial? source = plan.MaterialSource;
        ulong layoutVersion = plan.MaterialBindingLayoutVersion;
        ulong valueVersion = plan.MaterialBindingValueVersion;
        ulong resourceVersion = plan.MaterialBindingResourceVersion;
        long shaderRevision = plan.MaterialShaderStateRevision;
        long uberRevision = plan.MaterialUberStateRevision;
        uint flatDirection = plan.SourceFlatDirectionOct;
        uint flatValid = plan.SourceFlatDirectionValid;
        ulong structural = plan.StructuralSignature;
        ulong content = plan.ContentSignature;

        plan.MaterialSource = error;
        plan.MaterialBindingLayoutVersion = error.BindingLayoutVersion;
        plan.MaterialBindingValueVersion = error.BindingValueVersion;
        plan.MaterialBindingResourceVersion = error.BindingResourceVersion;
        plan.MaterialShaderStateRevision = error.ShaderStateRevision;
        plan.MaterialUberStateRevision = error.UberStateRevision;
        // The error material has no flat shadow mask, so the source basis has no effect.
        TryEncodeSourceFlatDirection(error, Matrix4x4.Identity,
            out plan.SourceFlatDirectionOct, out plan.SourceFlatDirectionValid);
        plan.StructuralSignature = Mix(Mix(ComputeStructuralSignature(
                in command, plan.Mesh, error, plan.PrimitiveIndex),
            plan.RenderState.Flags), plan.RenderState.CullMode);
        plan.ContentSignature = ComputeContentSignature(
            in command, in plan.Bounds, in plan.World, in plan.PreviousWorld,
            error, plan.GpuBoundsSource is not null,
            plan.SourceFlatDirectionOct, plan.SourceFlatDirectionValid);

        if (TryResolvePlannedMaterialCounting(
                error,
                in command,
                registrationIndex,
                out materialPlanIndex,
                out _,
                out fatal,
                out reason))
        {
            if (_errorMaterialRowCount++ == 0)
            {
                _firstErrorMaterialReason = sourceReason;
                _firstErrorMaterialText = sourceReasonText;
                _firstErrorMaterialSource = source;
                _firstErrorMaterialMesh = plan.Mesh;
            }
            return true;
        }

        plan.MaterialSource = source;
        plan.MaterialBindingLayoutVersion = layoutVersion;
        plan.MaterialBindingValueVersion = valueVersion;
        plan.MaterialBindingResourceVersion = resourceVersion;
        plan.MaterialShaderStateRevision = shaderRevision;
        plan.MaterialUberStateRevision = uberRevision;
        plan.SourceFlatDirectionOct = flatDirection;
        plan.SourceFlatDirectionValid = flatValid;
        plan.StructuralSignature = structural;
        plan.ContentSignature = content;
        return false;
    }

    /// <summary>
    /// Writes the error material rows of the last plan to <see cref="RenderReadinessFailureLog"/>
    /// (category <c>CanonicalErrorMaterial</c>), at most once per
    /// <see cref="ErrorMaterialReportRepeatTicks"/>. The line names the row count and the
    /// source material, mesh, compatibility reason and encoder text of the first row. Release
    /// builds also write this log. A plan without error rows costs one comparison.
    /// </summary>
    private void ReportErrorMaterialRows()
    {
        if (_errorMaterialRowCount == 0)
            return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_errorMaterialReportTimestamp != 0 &&
            now - _errorMaterialReportTimestamp < ErrorMaterialReportRepeatTicks)
            return;

        _errorMaterialReportTimestamp = now;
        RenderReadinessFailureLog.Report(
            "CanonicalErrorMaterial",
            nameof(AdvancedGpuScenePublisher),
            $"time={DateTimeOffset.Now:HH:mm:ss.fff} rows={_errorMaterialRowCount} " +
            $"firstMaterial='{_firstErrorMaterialSource?.Name ?? "<unnamed>"}' " +
            $"firstMesh='{_firstErrorMaterialMesh?.Name ?? "<unnamed>"}' " +
            $"firstCompatibilityReason={_firstErrorMaterialReason}",
            string.IsNullOrEmpty(_firstErrorMaterialText) ? "<none>" : _firstErrorMaterialText);
    }
}
