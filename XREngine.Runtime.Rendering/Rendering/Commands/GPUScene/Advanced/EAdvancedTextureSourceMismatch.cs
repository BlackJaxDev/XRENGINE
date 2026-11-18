namespace XREngine.Rendering.Commands;

/// <summary>
/// Why a backend source check found that a retained texture source of a canonical scene
/// publication no longer matches the published record. The Vulkan runtime reports each
/// failure as <c>SourceMismatch</c> and counts the reason in
/// <see cref="AdvancedTextureSourceMismatchDiagnostics"/>.
/// </summary>
public enum EAdvancedTextureSourceMismatch
{
    /// <summary>No mismatch.</summary>
    None,
    /// <summary>The publication did not retain every record and lookup image.</summary>
    RecordImageMissing,
    /// <summary>A texture row has a handle, id or generation that does not agree with its record.</summary>
    HandleMetadata,
    /// <summary>A texture row or material binding has no retained strong source reference.</summary>
    NoRetainedSource,
    /// <summary>The current source could not be encoded again, for example during a streaming write.</summary>
    RevalidationFailed,
    /// <summary>The content generation changed through a mutation that is not a streaming resident commit.</summary>
    ContentGenerationChanged,
    /// <summary>The dimension, flags, layer count or UV scale/bias changed.</summary>
    TextureShapeChanged,
    /// <summary>The format class changed outside its format family.</summary>
    TextureFormatChanged,
    /// <summary>The width, height or mip count changed without a streaming resident commit.</summary>
    TextureExtentOrMipsChanged,
    /// <summary>The sampler state changed.</summary>
    SamplerChanged,
    /// <summary>A material texture binding does not resolve its texture or sampler publication.</summary>
    BindingUnresolved,
    /// <summary>A material texture binding's sampler is absent from the retained dense image.</summary>
    BindingSamplerMissing,
    /// <summary>A sampler row has no texture or material binding that can check its source state.</summary>
    UnwitnessedSampler,
    /// <summary>Number of values.</summary>
    Count,
}
