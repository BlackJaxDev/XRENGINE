﻿using ImageMagick;
using System.Numerics;
using XREngine.Components;
using XREngine.Core;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Rendering.Commands;
using XREngine.Rendering.OpenGL;
using XREngine.Scene;
using static XREngine.Engine.Rendering.State;

namespace XREngine.Rendering;

/// <summary>
/// This class is the base class for all render pipelines.
/// A render pipeline is responsible for all rendering operations to render a scene to a viewport.
/// </summary>
public sealed partial class XRRenderPipelineInstance : XRBase
{
    public XRRenderPipelineInstance() { }
    public XRRenderPipelineInstance(RenderPipeline pipeline)
    {
        Pipeline = pipeline;
    }

    /// <summary>
    /// This collection contains mesh rendering commands pre-sorted for consuption by a render pass.
    /// </summary>
    public RenderCommandCollection MeshRenderCommands { get; } = new();

    private readonly Dictionary<string, XRTexture> _textures = [];
    private readonly Dictionary<string, XRFrameBuffer> _frameBuffers = [];

    private RenderPipeline? _pipeline;
    public RenderPipeline? Pipeline
    {
        get => _pipeline ?? SetFieldReturn(ref _pipeline, new DefaultRenderPipeline());
        set => SetField(ref _pipeline, value);
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

        foreach (XRTexture tex in _textures.Values)
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

        foreach (XRFrameBuffer fbo in _frameBuffers.Values)
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
                    MeshRenderCommands.SetRenderPasses(_pipeline.PassIndicesAndSorters);
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
        foreach (var fbo in _frameBuffers)
            fbo.Value.Destroy();
        _frameBuffers.Clear();

        foreach (var tex in _textures)
            tex.Value.Destroy();
        _textures.Clear();
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
        T? texture = null;
        if (_textures.TryGetValue(name, out XRTexture? value))
            texture = value as T;
        //if (texture is null)
        //    Debug.LogWarning($"Render pipeline texture {name} of type {typeof(T).GetFriendlyName()} was not found.");
        return texture;
    }

    public bool TryGetTexture(string name, out XRTexture? texture)
    {
        bool found = _textures.TryGetValue(name, out texture);
        //if (!found || texture is null)
        //    Debug.Out($"Render pipeline texture {name} was not found.");
        return found;
    }

    public void SetTexture(XRTexture texture)
    {
        string? name = texture.Name;
        if (name is null)
        {
            Debug.LogWarning("Texture name must be set before adding to the pipeline.");
            return;
        }
        if (!_textures.TryAdd(name, texture))
        {
            Debug.Out($"Render pipeline texture {name} already exists. Overwriting.");
            _textures[name]?.Destroy();
            _textures[name] = texture;
        }
    }

    public T? GetFBO<T>(string name) where T : XRFrameBuffer
    {
        T? fbo = null;
        if (_frameBuffers.TryGetValue(name, out XRFrameBuffer? value))
            fbo = value as T;
        //if (fbo is null)
        //    Debug.LogWarning($"Render pipeline FBO {name} of type {typeof(T).GetFriendlyName()} was not found.");
        return fbo;
    }

    public bool TryGetFBO(string name, out XRFrameBuffer? fbo)
    {
        bool found = _frameBuffers.TryGetValue(name, out fbo);
        //if (!found || fbo is null)
        //    Debug.Out($"Render pipeline FBO {name} was not found.");
        return found;
    }

    public void SetFBO(XRFrameBuffer fbo)
    {
        string? name = fbo.Name;
        if (name is null)
        {
            Debug.LogWarning("FBO name must be set before adding to the pipeline.");
            return;
        }
        if (!_frameBuffers.TryAdd(name, fbo))
        {
            Debug.Out($"Render pipeline FBO {name} already exists. Overwriting.");
            _frameBuffers[name]?.Destroy();
            _frameBuffers[name] = fbo;
        }
    }
}