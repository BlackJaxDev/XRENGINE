using ImageMagick;
using System.Collections.Generic;
using System.Numerics;
using XREngine.Components;
using XREngine.Core;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Rendering.Commands;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Resources;
using XREngine.Scene;
using static XREngine.Engine.Rendering.State;

namespace XREngine.Rendering;

/// <summary>
/// This class is the base class for all render pipelines.
/// A render pipeline is responsible for all rendering operations to render a scene to a viewport.
/// </summary>
public sealed partial class XRRenderPipelineInstance : XRBase
{
    public XRRenderPipelineInstance()
    {
        MeshRenderCommands.SetOwnerPipeline(this);
    }

    public XRRenderPipelineInstance(RenderPipeline pipeline) : this()
    {
        Pipeline = pipeline;
    }

    /// <summary>
    /// This collection contains mesh rendering commands pre-sorted for consuption by a render pass.
    /// </summary>
    public RenderCommandCollection MeshRenderCommands { get; } = new();

    public RenderResourceRegistry Resources { get; } = new();

    // Track the last applied internal resolution scale to avoid resetting the viewport every frame.
    private float? _appliedInternalResolutionScale;

    private RenderPipeline? _pipeline;
    public RenderPipeline? Pipeline
    {
        get => _pipeline ?? SetFieldReturn(ref _pipeline, Engine.Rendering.NewRenderPipeline());
        set => SetField(ref _pipeline, value);
    }

    /// <summary>
    /// Builds a human-readable descriptor for debugging the active pipeline state.
    /// </summary>
    public string DebugDescriptor
    {
        get
        {
            RenderPipeline? pipeline = _pipeline ?? Pipeline;
            string pipelineName = pipeline?.DebugName ?? "UnknownPipeline";

            string cameraDescription = RenderState.SceneCamera is { } cam
                ? DescribeCamera(cam)
                : "Camera=<none>";

            string viewportDescription = RenderState.WindowViewport is { } viewport
                ? DescribeViewport(viewport)
                : "Viewport=<none>";

            string extraTags = BuildTagString(RenderState.ShadowPass, RenderState.StereoPass);

            return string.IsNullOrEmpty(extraTags)
                ? $"{pipelineName} | {cameraDescription} | {viewportDescription}"
                : $"{pipelineName} | {cameraDescription} | {viewportDescription} {extraTags}";
        }
    }

    private static string DescribeCamera(XRCamera cam)
    {
        string typeName = cam.GetType().Name;
        string? nodeName = cam.Transform.SceneNode?.Name;
        if (!string.IsNullOrWhiteSpace(nodeName))
            return $"Camera={nodeName} ({typeName})";
        return $"Camera={typeName}";
    }

    private static string DescribeViewport(XRViewport viewport)
    {
        string? baseName = viewport.Window?.Window?.Title;
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "Viewport";
        return $"Viewport={baseName}#{viewport.Index} ({viewport.Width}x{viewport.Height})";
    }

    private static string BuildTagString(bool isShadow, bool isStereo)
    {
        var tags = new List<string>(2);
        if (isShadow)
            tags.Add("Shadow");
        if (isStereo)
            tags.Add("Stereo");

        return tags.Count == 0 ? string.Empty : $"[{string.Join(",", tags)}]";
    }

    /// <summary>
    /// Captures all textures in the pipeline and saves them to the specified directory.
    /// This method must be called from the main thread - it's recommended to use the 'PostRender' default draw pass to call this method.
    /// </summary>
    /// <param name="exportDirPath"></param>
    public void CaptureAllTextures(string exportDirPath)
    {
        if (AbstractRenderer.Current is not OpenGLRenderer rend)
            return;

        foreach (XRTexture tex in Resources.EnumerateTextureInstances())
        {
            if (tex.APIWrappers.FirstOrDefault(x => x is IGLTexture) is not IGLTexture apiWrapper)
                continue;

            var whd = apiWrapper.WidthHeightDepth;
            BoundingRectangle region = new(0, 0, (int)whd.X, (int)whd.Y);
            for (int i = 0; i < whd.Z; i++)
            {
                void ProcessImage(MagickImage image, int layer, int channelIndex)
                {
                    string name = tex.Name ?? tex.GetDescribingName();
                    if (whd.Z > 1)
                        name += $" [Layer {layer + 1}]";
                    if (channelIndex > 0)
                        name += $" [{channelIndex + 1}]";
                    string fileName = $"{name}.png";
                    string filePath = Path.Combine(exportDirPath, fileName);
                    Utility.EnsureDirPathExists(exportDirPath);
                    image.Flip();
                    image.Write(filePath);
                }

                rend.CaptureTexture(region, ProcessImage, apiWrapper.BindingId, 0, i);
            }
        }
    }

    /// <summary>
    /// Captures all framebuffers in the pipeline and saves them to the specified directory.
    /// This method must be called from the main thread - it's recommended to use the 'PostRender' default draw pass to call this method.
    /// </summary>
    /// <param name="exportDirPath"></param>
    public void CaptureAllFBOs(string exportDirPath)
    {
        if (AbstractRenderer.Current is not OpenGLRenderer rend)
            return;

        foreach (XRFrameBuffer fbo in Resources.EnumerateFrameBufferInstances())
        {
            if (fbo.Targets is null || 
                fbo.Targets.Length == 0 || 
                fbo.APIWrappers.FirstOrDefault(x => x is GLFrameBuffer) is not GLFrameBuffer apiWrapper)
                continue;

            foreach (var (Target, Attachment, MipLevel, LayerIndex) in fbo.Targets)
            {
                void ProcessImage(MagickImage image, int index)
                {
                    string name = $"{fbo.GetDescribingName()}_{Attachment}";
                    if (index > 0)
                        name += $"_img{index + 1}";
                    if (MipLevel > 0)
                        name += $"_mip{MipLevel}";
                    if (LayerIndex >= 0)
                        name += $"_layer{LayerIndex}";
                    string fileName = $"{name}.png";
                    string filePath = Path.Combine(exportDirPath, fileName);
                    Utility.EnsureDirPathExists(exportDirPath);
                    image.Flip();
                    image.Write(filePath);
                }

                switch (Target)
                {
                    case XRTexture2D tex2D:
                        {
                            BoundingRectangle region = new(0, 0, (int)tex2D.Width, (int)tex2D.Height);
                            rend.CaptureFBOAttachment(region, true, ProcessImage, apiWrapper.BindingId, Attachment);
                        }
                        break;
                    case XRTexture2DArray tex2DArray:
                        for (int i = 0; i < tex2DArray.Depth; ++i)
                        {
                            BoundingRectangle region = new(0, 0, (int)tex2DArray.Width, (int)tex2DArray.Height);
                            rend.CaptureFBOAttachment(region, true, ProcessImage, apiWrapper.BindingId, Attachment);
                        }
                        break;
                }    

            }
        }
    }

    protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
    {
        bool change = base.OnPropertyChanging(propName, field, @new);
        if (change)
        {
            switch (propName)
            {
                case nameof(Pipeline):
                    _pipeline?.Instances.Remove(this);
                    break;
            }
        }
        return change;
    }
    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);
        switch (propName)
        {
            case nameof(Pipeline):
                if (_pipeline is not null)
                {
                    MeshRenderCommands.SetRenderPasses(_pipeline.PassIndicesAndSorters, _pipeline.PassMetadata);
                    InvalidMaterial = _pipeline.InvalidMaterial;
                    _pipeline.Instances.Add(this);
                }
                else
                    InvalidMaterial = null;
                DestroyCache();
                break;
        }
    }
    
    public RenderingState CollectVisibleState { get; } = new();
    public RenderingState RenderState { get; } = new();
    public XRMaterial? InvalidMaterial { get; set; }

    /// <summary>
    /// Renders the scene to the viewport or framebuffer.
    /// </summary>
    /// <param name="scene"></param>
    /// <param name="camera"></param>
    /// <param name="viewport"></param>
    /// <param name="targetFBO"></param>
    /// <param name="shadowPass"></param>
    public void Render(
        VisualScene scene,
        XRCamera? camera,
        XRCamera? stereoRightEyeCamera,
        XRViewport? viewport,
        XRFrameBuffer? targetFBO = null,
        UICanvasComponent? userInterface = null,
        bool shadowPass = false,
        bool stereoPass = false,
        XRMaterial? shadowMaterial = null,
        RenderCommandCollection? meshRenderCommandsOverride = null)
    {
        if (Pipeline is null)
        {
            Debug.LogWarning("No render pipeline is set.");
            return;
        }

        // Honor any internal resolution request from the pipeline before executing commands.
        if (viewport is not null)
        {
            float? requestedScale = Pipeline.RequestedInternalResolution;

            // Avoid redundant resets: only touch the viewport when the requested scale changes.
            if (requestedScale.HasValue)
            {
                float scale = Math.Clamp(requestedScale.Value, 0.25f, 1.25f);
                if (_appliedInternalResolutionScale != scale)
                {
                    _appliedInternalResolutionScale = scale;
                    viewport.SetInternalResolutionPercentage(scale, scale);
                }
            }
            else if (_appliedInternalResolutionScale.HasValue)
            {
                // Restore to native internal resolution once the request is cleared.
                _appliedInternalResolutionScale = null;
                viewport.SetInternalResolution(viewport.Width, viewport.Height, true);
            }
        }

        using (PushRenderingPipeline(this))
        {
            using (RenderState.PushMainAttributes(viewport, scene, camera, stereoRightEyeCamera, targetFBO, shadowPass, stereoPass, shadowMaterial, userInterface, meshRenderCommandsOverride ?? MeshRenderCommands))
            {
                Pipeline.CommandChain.Execute();
            }
        }
    }
    //public void CollectVisible(VisualScene scene, XRCamera? camera, XRViewport viewport, XRFrameBuffer? targetFBO, bool shadowPass, UICanvasComponent? userInterface = null)
    //{
    //    if (Pipeline is null)
    //    {
    //        Debug.LogWarning("No render pipeline is set.");
    //        return;
    //    }
    //    using (PushRenderingPipeline(this))
    //    {
    //        using (CollectVisibleState.PushMainAttributes(viewport, scene, camera, targetFBO, shadowPass, null, userInterface))
    //        {
    //            scene.GlobalCollectVisible();
    //        }
    //    }
    //}

    public void DestroyCache()
    {
        Resources.DestroyAllPhysicalResources();
    }

    public void ViewportResized(Vector2 size)
    {
        //DestroyCache();
    }
    public void ViewportResized(int width, int height)
    {
        //DestroyCache();
    }
    public void InternalResolutionResized(int internalWidth, int internalHeight)
    {
        //We only need to destroy the internal resolution data
        DestroyCache();
    }

    public T? GetTexture<T>(string name) where T : XRTexture
    {
        if (Resources.TryGetTexture(name, out XRTexture? value))
            return value as T;
        return null;
    }

    public bool TryGetTexture(string name, out XRTexture? texture)
    {
        return Resources.TryGetTexture(name, out texture);
    }

    public void SetTexture(XRTexture texture, TextureResourceDescriptor? descriptor = null)
    {
        string? name = texture.Name;
        if (name is null)
        {
            Debug.LogWarning("Texture name must be set before adding to the pipeline.");
            return;
        }
        Resources.BindTexture(texture, descriptor);
    }

    public T? GetFBO<T>(string name) where T : XRFrameBuffer
    {
        if (Resources.TryGetFrameBuffer(name, out XRFrameBuffer? value))
            return value as T;
        return null;
    }

    public bool TryGetFBO(string name, out XRFrameBuffer? fbo)
    {
        return Resources.TryGetFrameBuffer(name, out fbo);
    }

    public void SetFBO(XRFrameBuffer fbo, FrameBufferResourceDescriptor? descriptor = null)
    {
        string? name = fbo.Name;
        if (name is null)
        {
            Debug.LogWarning("FBO name must be set before adding to the pipeline.");
            return;
        }
        Resources.BindFrameBuffer(fbo, descriptor);
    }
}