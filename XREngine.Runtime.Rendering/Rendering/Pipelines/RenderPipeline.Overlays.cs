using System.ComponentModel;
using System.Reflection;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Pipelines.Overlays;
using XREngine.Rendering.Pipelines.Validation;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.Resources;
using YamlDotNet.Serialization;

namespace XREngine.Rendering;

// Derived pipeline overlays: patches on top of the base command chain, resource layout, and schema.
public abstract partial class RenderPipeline
{
    private IReadOnlyList<RenderPipelineOverlay> _overlays = [];
    private RenderPipelineOverlayReport _overlayReport = RenderPipelineOverlayReport.Empty;
    private ulong _overlayResourceHash;

    /// <summary>
    /// Gets or sets the overlay chain of a derived pipeline, root overlay first. The pipeline applies the command patches
    /// after <see cref="GenerateCommandChain"/>, the resource patches after <see cref="DescribeResources"/>, and the
    /// schema patches after <see cref="DescribePostProcessSchema"/>. Set a new list to apply a change.
    /// </summary>
    [Browsable(false), YamlIgnore]
    public IReadOnlyList<RenderPipelineOverlay> Overlays
    {
        get => _overlays;
        set
        {
            _overlays = value ?? [];
            ulong hash = 0UL;
            for (int i = 0; i < _overlays.Count; i++)
                hash = RenderPipelineStructureHash.Mix(hash ^ _overlays[i].ComputeResourceHash(), "overlay");
            _overlayResourceHash = _overlays.Count == 0 ? 0UL : hash;
            if (_overlays.Count == 0)
                _overlayReport = RenderPipelineOverlayReport.Empty;
            RefreshAfterOverlayChange();
        }
    }

    /// <summary>Gets whether the pipeline has overlays, so that it is a derived pipeline.</summary>
    [Browsable(false), YamlIgnore]
    public bool IsDerived => _overlays.Count > 0;

    /// <summary>Gets the result of the last overlay apply.</summary>
    [Browsable(false), YamlIgnore]
    public RenderPipelineOverlayReport OverlayReport => _overlayReport;

    /// <summary>
    /// Gets the anchor renames of this pipeline: old anchor id to new anchor id. A base that restructures its chain
    /// records each rename here, so that existing patches still find their targets.
    /// </summary>
    [Browsable(false), YamlIgnore]
    public Dictionary<string, string> AnchorAliases { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the hash of the resource patches. The resource generation key includes it.</summary>
    internal ulong OverlayResourceHash => _overlayResourceHash;

    /// <summary>
    /// Computes the structure hash of the base pipeline with the first <paramref name="overlayCount"/> overlays:
    /// anchors, command types, resource names, and stage keys. The resource names come from the default validation
    /// profile. A derived script stores the hash of the layers below its own overlay.
    /// </summary>
    public ulong ComputeBaseStructureHash(int overlayCount = 0)
    {
        overlayCount = Math.Clamp(overlayCount, 0, _overlays.Count);
        List<RenderPipelineOverlay> layers = new(overlayCount);
        for (int i = 0; i < overlayCount; i++)
            layers.Add(_overlays[i]);

        List<RenderPipelinePatchResult> ignored = [];
        ViewportRenderCommandContainer chain = GenerateCommandChainForOverlays();
        if (layers.Count > 0 && !RenderPipelineOverlayApplier.ApplyCommands(this, chain, layers, AnchorAliases, ignored, new(ReferenceEqualityComparer.Instance), []))
            chain = GenerateCommandChainForOverlays();

        RenderPipelineResourceLayout? layout = null;
        try
        {
            RenderPipelineResourceLayoutBuilder builder = new(RenderPipelineValidationContext.DefaultProfile);
            DescribeResources(builder);
            RenderPipelineOverlayApplier.ApplyResources(builder, layers, ResolveOverlayAttachment, ignored);
            layout = builder.Build(RenderPipelineValidationContext.DefaultProfile);
        }
        catch (InvalidOperationException)
        {
        }

        RenderPipelinePostProcessSchemaBuilder schemaBuilder = new(this);
        DescribePostProcessSchema(schemaBuilder);
        RenderPipelineOverlayApplier.ApplySchema(schemaBuilder, layers, ignored);
        return RenderPipelineStructureHash.Compute(chain, layout, schemaBuilder.Build());
    }

    /// <summary>
    /// Returns a new base command chain for an overlay apply. The patches change the chain in place, so a pipeline
    /// whose <see cref="GenerateCommandChain"/> returns a stored container must return a copy.
    /// </summary>
    protected virtual ViewportRenderCommandContainer GenerateCommandChainForOverlays()
        => GenerateCommandChain();

    /// <summary>Applies each overlay setting override through the property setters.</summary>
    /// <returns>One error text for each override that failed.</returns>
    public IReadOnlyList<string> ApplyOverlaySettings()
    {
        List<string> errors = [];
        Type type = GetType();
        for (int o = 0; o < _overlays.Count; o++)
        {
            foreach ((string name, string valueText) in _overlays[o].SettingOverrides)
            {
                PropertyInfo? property = RenderPipelineScriptSettings.FindSetting(type, name);
                if (property is null)
                {
                    errors.Add(string.Concat(type.Name, " has no setting '", name, "'."));
                    continue;
                }

                try
                {
                    property.SetValue(this, RenderPipelineScript.ConvertValueText(valueText, property.PropertyType));
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or TargetInvocationException)
                {
                    errors.Add(string.Concat("Setting '", name, "' failed: ", ex.InnerException?.Message ?? ex.Message));
                }
            }
        }

        return errors;
    }

    /// <summary>Builds the command chain with the overlays. A failed overlay gives the base chain and a report with the failure.</summary>
    private ViewportRenderCommandContainer GenerateChainWithOverlays()
    {
        if (_overlays.Count == 0)
            return GenerateCommandChain();

        List<RenderPipelinePatchResult> results = [];
        Dictionary<ViewportRenderCommand, ERenderCommandPatchKind> markers = new(ReferenceEqualityComparer.Instance);
        List<string> removed = [];
        ViewportRenderCommandContainer chain = GenerateCommandChainForOverlays();
        bool applied = RenderPipelineOverlayApplier.ApplyCommands(this, chain, _overlays, AnchorAliases, results, markers, removed);
        if (!applied)
        {
            // Never render part of an overlay. A fresh base chain has none of the patches that ran before the failure.
            chain = GenerateCommandChainForOverlays();
            markers.Clear();
            removed.Clear();
            Debug.RenderingWarning($"[{DebugName}] A derived pipeline patch failed. The base chain renders without the overlays.");
        }

        _overlayReport = _overlayReport.WithCommands(results, applied, markers, removed);
        return chain;
    }

    /// <summary>Applies the resource patches to <paramref name="builder"/>.</summary>
    private void ApplyOverlayResources(RenderPipelineResourceLayoutBuilder builder)
    {
        if (_overlays.Count == 0)
            return;

        List<RenderPipelinePatchResult> results = [];
        bool applied = RenderPipelineOverlayApplier.ApplyResources(builder, _overlays, ResolveOverlayAttachment, results);
        _overlayReport = _overlayReport.WithResources(results, applied);
    }

    /// <summary>Applies the schema patches to <paramref name="builder"/>.</summary>
    private void ApplyOverlaySchema(RenderPipelinePostProcessSchemaBuilder builder)
    {
        if (_overlays.Count == 0)
            return;

        List<RenderPipelinePatchResult> results = [];
        bool applied = RenderPipelineOverlayApplier.ApplySchema(builder, _overlays, results);
        _overlayReport = _overlayReport.WithSchema(results, applied);
    }

    private static IFrameBufferAttachement? ResolveOverlayAttachment(string name)
        => GetTexture<XRTexture>(name) as IFrameBufferAttachement ?? TryCurrentPipeline?.GetRenderBuffer(name);

    private void RefreshAfterOverlayChange()
    {
        if (Instances.Count == 0)
            InitializeCommandChain();
        else
            RebuildCommandChain();
        RefreshPostProcessSchema();
    }
}
