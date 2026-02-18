using Extensions;
using System;
using System.Numerics;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.Occlusion;
using XREngine.Rendering.RenderGraph;
using XREngine.Scene;

namespace XREngine.Rendering.Commands
{
    public class FarToNearRenderCommandSorter : IComparer<RenderCommand>
    {
        int IComparer<RenderCommand>.Compare(RenderCommand? x, RenderCommand? y)
            => -(x?.CompareTo(y) ?? 0);
    }
    public class NearToFarRenderCommandSorter : IComparer<RenderCommand>
    {
        int IComparer<RenderCommand>.Compare(RenderCommand? x, RenderCommand? y)
            => x?.CompareTo(y) ?? 0;
    }

    /// <summary>
    /// This class is used to manage the rendering of objects in the scene.
    /// RenderCommands are collected and placed in sorted passes that are rendered in order.
    /// At the end of the render and update loop, the buffers are swapped for consumption and the update list is cleared for the next frame.
    /// </summary>
    public sealed class RenderCommandCollection : XRBase
    {
        private static readonly CpuRenderOcclusionCoordinator s_cpuOcclusionCoordinator = new();

        public bool IsShadowPass { get; private set; } = false;
        public void SetRenderPasses(Dictionary<int, IComparer<RenderCommand>?> passIndicesAndSorters, IEnumerable<RenderPassMetadata>? passMetadata = null)
        {
            using (_lock.EnterScope())
            {
                _updatingPasses = passIndicesAndSorters.ToDictionary(x => x.Key, x => x.Value is null ? [] : (ICollection<RenderCommand>)new SortedSet<RenderCommand>(x.Value));

                _renderingPasses = [];
                _gpuPasses = [];

                // PassMetadata should be unique by PassIndex, but during restore/re-init we may end up with duplicates.
                // Avoid crashing the entire engine; keep the first entry and warn.
                _passMetadata = [];
                if (passMetadata is not null)
                {
                    foreach (var meta in passMetadata)
                    {
                        if (_passMetadata.TryAdd(meta.PassIndex, meta))
                            continue;

                        var existing = _passMetadata[meta.PassIndex];
                        Debug.RenderingWarningEvery(
                            $"RenderPassMetadata.Duplicate.{meta.PassIndex}",
                            TimeSpan.FromSeconds(5),
                            "[RenderDiag] Duplicate RenderPassMetadata PassIndex={0}. Keeping first ('{1}', Stage={2}), ignoring ('{3}', Stage={4}).",
                            meta.PassIndex,
                            existing.Name,
                            existing.Stage,
                            meta.Name,
                            meta.Stage);
                    }
                }

                foreach (KeyValuePair<int, ICollection<RenderCommand>> pass in _updatingPasses)
                {
                    // Use TryAdd to safely handle any edge cases with duplicate keys
                    _renderingPasses.TryAdd(pass.Key, []);
                    
                    if (!_gpuPasses.ContainsKey(pass.Key))
                    {
                        var gpuPass = new GPURenderPassCollection(pass.Key);
                        gpuPass.SetDebugContext(_ownerPipeline, pass.Key);
                        _gpuPasses[pass.Key] = gpuPass;
                    }

                    if (!_passMetadata.ContainsKey(pass.Key))
                        _passMetadata[pass.Key] = new RenderPassMetadata(pass.Key, $"Pass{pass.Key}", ERenderGraphPassStage.Graphics);
                }
            }
        }

        private int _numCommandsRecentlyAddedToUpdate = 0;

        private Dictionary<int, ICollection<RenderCommand>> _updatingPasses = [];
        private Dictionary<int, ICollection<RenderCommand>> _renderingPasses = [];
        private Dictionary<int, GPURenderPassCollection> _gpuPasses = [];
        private Dictionary<int, RenderPassMetadata> _passMetadata = [];
        private XRRenderPipelineInstance? _ownerPipeline;

        public RenderCommandCollection() { }
        public RenderCommandCollection(Dictionary<int, IComparer<RenderCommand>?> passIndicesAndSorters)
            => SetRenderPasses(passIndicesAndSorters);

        internal void SetOwnerPipeline(XRRenderPipelineInstance pipeline)
        {
            _ownerPipeline = pipeline;
            foreach (KeyValuePair<int, GPURenderPassCollection> pair in _gpuPasses)
                pair.Value.SetDebugContext(_ownerPipeline, pair.Key);
        }

        private readonly Lock _lock = new();

        public int GetUpdatingCommandCount()
        {
            using (_lock.EnterScope())
                return _updatingPasses.Values.Sum(static pass => pass.Count);
        }

        public int GetRenderingCommandCount()
        {
            using (_lock.EnterScope())
                return _renderingPasses.Values.Sum(static pass => pass.Count);
        }

        public void AddRangeCPU(IEnumerable<RenderCommand> renderCommands)
        {
            foreach (RenderCommand renderCommand in renderCommands)
                AddCPU(renderCommand);
        }
        public void AddCPU(RenderCommand item)
        {
            int pass = item.RenderPass;
            if (!_updatingPasses.TryGetValue(pass, out var set))
                return; // No CPU pass found for this render command

            using (_lock.EnterScope())
            {
                set.Add(item);
                ++_numCommandsRecentlyAddedToUpdate;
            }
        }

        public int GetCommandsAddedCount()
        {
            int added = _numCommandsRecentlyAddedToUpdate;
            _numCommandsRecentlyAddedToUpdate = 0;
            return added;
        }

        public void RenderCPU(int renderPass, bool skipGpuCommands = false, XRCamera? camera = null)
        {
            if (!_renderingPasses.TryGetValue(renderPass, out ICollection<RenderCommand>? list))
                return;

            bool useCpuOcclusion =
                !Engine.Rendering.State.IsShadowPass &&
                camera is not null &&
                Engine.EffectiveSettings.GpuOcclusionCullingMode == EOcclusionCullingMode.CpuQueryAsync;

            if (useCpuOcclusion)
                s_cpuOcclusionCoordinator.BeginPass(renderPass, camera!, (uint)list.Count);

            uint cpuCmdIndex = 0;
            foreach (var cmd in list)
            {
                if (skipGpuCommands && cmd is IRenderCommandMesh meshCmd)
                {
                    // Skip mesh commands that should go through GPU dispatch,
                    // but still render meshes that explicitly exclude themselves from GPU indirect.
                    var material = meshCmd.MaterialOverride ?? meshCmd.Mesh?.Material;
                    if (material?.RenderOptions?.ExcludeFromGpuIndirect != true)
                    {
                        cpuCmdIndex++;
                        continue;
                    }
                }

                if (useCpuOcclusion && cmd is IRenderCommandMesh)
                {
                    // Use the loop index as the key — GPUCommandIndex is uint.MaxValue
                    // for all commands in pure CPU mode, which would cause every command
                    // to share a single query state.
                    if (!s_cpuOcclusionCoordinator.ShouldRender(renderPass, cpuCmdIndex))
                    {
                        cpuCmdIndex++;
                        continue;
                    }

                    s_cpuOcclusionCoordinator.BeginQuery(renderPass, cpuCmdIndex);
                    try
                    {
                        cmd?.Render();
                    }
                    finally
                    {
                        s_cpuOcclusionCoordinator.EndQuery(renderPass, cpuCmdIndex);
                    }

                    cpuCmdIndex++;
                    continue;
                }

                cpuCmdIndex++;

                cmd?.Render();
            }
        }

        public void RenderCPUMeshOnly(int renderPass)
        {
            if (!_renderingPasses.TryGetValue(renderPass, out ICollection<RenderCommand>? list))
                return;

            foreach (var cmd in list)
            {
                if (cmd is IRenderCommandMesh)
                    cmd?.Render();
            }
        }
        public void RenderGPU( int renderPass)
        {
            if (!_gpuPasses.TryGetValue(renderPass, out GPURenderPassCollection? gpuPass))
                return;
            
            var renderState = Engine.Rendering.State.CurrentRenderingPipeline?.RenderState;
            if (renderState is null)
                return;

            XRCamera? camera = renderState.SceneCamera;
            if (camera is null)
                return;

            var scene = renderState.RenderingScene;
            if (scene is null)
                return;

            ConfigureGpuViewSet(gpuPass, renderState, camera);

            gpuPass.Render(scene.GPUCommands);

            if (scene is VisualScene3D visualScene)
            {
                gpuPass.GetVisibleCounts(out uint draws, out uint instances, out _);
                visualScene.RecordGpuVisibility(draws, instances);

                bool allowPerViewReadback = Engine.EffectiveSettings.EnableGpuIndirectDebugLogging;
                if (allowPerViewReadback && gpuPass.ActiveViewCount > 0)
                {
                    uint leftDraws = gpuPass.ReadPerViewDrawCount(0u);
                    uint rightDraws = gpuPass.ActiveViewCount > 1u
                        ? gpuPass.ReadPerViewDrawCount(1u)
                        : 0u;
                    Engine.Rendering.Stats.RecordVrPerViewDrawCounts(leftDraws, rightDraws);
                }
            }
        }

        private static void ConfigureGpuViewSet(GPURenderPassCollection gpuPass, XRRenderPipelineInstance.RenderingState renderState, XRCamera leftCamera)
        {
            var settings = Engine.Rendering.Settings;
            XRCamera? rightCamera = renderState.StereoPass ? renderState.StereoRightEyeCamera : null;
            bool stereo = renderState.StereoPass && rightCamera is not null;
            bool includeMirror = settings.RenderWindowsWhileInVR && !settings.VrMirrorComposeFromEyeTextures;
            bool includeFoveated = stereo && settings.EnableVrFoveatedViewSet;
            float foveationOuter = Math.Clamp(
                settings.VrFoveationOuterRadius + settings.VrFoveationVisibilityMargin,
                settings.VrFoveationInnerRadius,
                1.5f);
            float fullResNearDistance = settings.VrFoveationForceFullResForUiAndNearField
                ? settings.VrFoveationFullResNearDistanceMeters
                : 0.0f;

            int viewCount = stereo ? 2 : 1;
            if (includeFoveated)
                viewCount += 2;
            if (includeMirror)
                viewCount += 1;

            Span<GPUViewDescriptor> descriptors = stackalloc GPUViewDescriptor[5];
            Span<GPUViewConstants> constants = stackalloc GPUViewConstants[5];

            int width = Math.Max(1, renderState.WindowViewport?.InternalWidth ?? renderState.WindowViewport?.Width ?? 1);
            int height = Math.Max(1, renderState.WindowViewport?.InternalHeight ?? renderState.WindowViewport?.Height ?? 1);

            uint passBit = gpuPass.RenderPass >= 0 && gpuPass.RenderPass < 32
                ? (1u << gpuPass.RenderPass)
                : uint.MaxValue;

            uint cursor = 0u;
            descriptors[(int)cursor] = CreateViewDescriptor(
                cursor,
                GPUViewSetLayout.InvalidViewId,
                GPUViewFlags.StereoEyeLeft | GPUViewFlags.FullRes | GPUViewFlags.UsesSharedVisibility,
                passBit,
                0u,
                0,
                0,
                width,
                height,
                0u,
                gpuPass.CommandCapacity);
            constants[(int)cursor] = CreateViewConstants(leftCamera);
            cursor++;

            if (stereo && rightCamera is not null)
            {
                descriptors[(int)cursor] = CreateViewDescriptor(
                    cursor,
                    GPUViewSetLayout.InvalidViewId,
                    GPUViewFlags.StereoEyeRight | GPUViewFlags.FullRes | GPUViewFlags.UsesSharedVisibility,
                    passBit,
                    1u,
                    0,
                    0,
                    width,
                    height,
                    gpuPass.CommandCapacity,
                    gpuPass.CommandCapacity);
                constants[(int)cursor] = CreateViewConstants(rightCamera);
                cursor++;
            }

            if (includeFoveated)
            {
                descriptors[(int)cursor] = CreateViewDescriptor(
                    cursor,
                    0u,
                    GPUViewFlags.StereoEyeLeft | GPUViewFlags.Foveated | GPUViewFlags.UsesSharedVisibility,
                    passBit,
                    0u,
                    0,
                    0,
                    width,
                    height,
                    gpuPass.CommandCapacity * cursor,
                    gpuPass.CommandCapacity);
                descriptors[(int)cursor].FoveationA = new Vector4(
                    settings.VrFoveationCenterUv,
                    settings.VrFoveationInnerRadius,
                    foveationOuter);
                descriptors[(int)cursor].FoveationB = new Vector4(
                    settings.VrFoveationShadingRates,
                    fullResNearDistance);
                constants[(int)cursor] = CreateViewConstants(leftCamera);
                cursor++;

                if (rightCamera is not null)
                {
                    descriptors[(int)cursor] = CreateViewDescriptor(
                        cursor,
                        1u,
                        GPUViewFlags.StereoEyeRight | GPUViewFlags.Foveated | GPUViewFlags.UsesSharedVisibility,
                        passBit,
                        1u,
                        0,
                        0,
                        width,
                        height,
                        gpuPass.CommandCapacity * cursor,
                        gpuPass.CommandCapacity);
                    descriptors[(int)cursor].FoveationA = new Vector4(
                        settings.VrFoveationCenterUv,
                        settings.VrFoveationInnerRadius,
                        foveationOuter);
                    descriptors[(int)cursor].FoveationB = new Vector4(
                        settings.VrFoveationShadingRates,
                        fullResNearDistance);
                    constants[(int)cursor] = CreateViewConstants(rightCamera);
                    cursor++;
                }
            }

            if (includeMirror)
            {
                descriptors[(int)cursor] = CreateViewDescriptor(
                    cursor,
                    0u,
                    GPUViewFlags.Mirror | GPUViewFlags.UsesSharedVisibility,
                    passBit,
                    0u,
                    0,
                    0,
                    width,
                    height,
                    gpuPass.CommandCapacity * cursor,
                    gpuPass.CommandCapacity);
                constants[(int)cursor] = CreateViewConstants(leftCamera);
                cursor++;
            }

            ValidateViewDescriptorLayout(descriptors.Slice(0, (int)cursor), gpuPass.CommandCapacity);
            gpuPass.ConfigureViewSet(descriptors.Slice(0, (int)cursor), constants.Slice(0, (int)cursor));
            uint requestedSourceView = DetermineIndirectSourceViewId(renderState, leftCamera, rightCamera);
            gpuPass.SetIndirectSourceViewId(requestedSourceView);

            if (requestedSourceView != gpuPass.IndirectSourceViewId)
            {
                throw new InvalidOperationException(
                    $"Indirect source view id {requestedSourceView} was clamped to {gpuPass.IndirectSourceViewId} for active views {gpuPass.ActiveViewCount}.");
            }
        }

        private static void ValidateViewDescriptorLayout(ReadOnlySpan<GPUViewDescriptor> descriptors, uint commandCapacity)
        {
            uint expectedOffset = 0u;
            for (int i = 0; i < descriptors.Length; i++)
            {
                GPUViewDescriptor descriptor = descriptors[i];
                if (descriptor.ViewId != (uint)i)
                    throw new InvalidOperationException($"View descriptor order mismatch at index {i}; found ViewId={descriptor.ViewId}.");

                if (descriptor.VisibleOffset != expectedOffset)
                {
                    throw new InvalidOperationException(
                        $"View {descriptor.ViewId} visible offset {descriptor.VisibleOffset} does not match expected {expectedOffset}.");
                }

                if (descriptor.VisibleCapacity != commandCapacity)
                {
                    throw new InvalidOperationException(
                        $"View {descriptor.ViewId} visible capacity {descriptor.VisibleCapacity} does not match command capacity {commandCapacity}.");
                }

                expectedOffset += commandCapacity;
            }
        }

        private static uint DetermineIndirectSourceViewId(
            XRRenderPipelineInstance.RenderingState renderState,
            XRCamera sceneCamera,
            XRCamera? stereoRightCamera)
        {
            if (!renderState.StereoPass || stereoRightCamera is null)
                return 0u;

            if (sceneCamera.Parameters is XROVRCameraParameters ovrParams)
                return ovrParams.LeftEye ? 0u : 1u;

            if (ReferenceEquals(sceneCamera, stereoRightCamera))
                return 1u;

            return 0u;
        }

        private static GPUViewDescriptor CreateViewDescriptor(
            uint viewId,
            uint parentViewId,
            GPUViewFlags flags,
            uint passMaskLo,
            uint outputLayer,
            int rectX,
            int rectY,
            int rectW,
            int rectH,
            uint visibleOffset,
            uint visibleCapacity)
        {
            return new GPUViewDescriptor
            {
                ViewId = viewId,
                ParentViewId = parentViewId,
                Flags = (uint)flags,
                RenderPassMaskLo = passMaskLo,
                RenderPassMaskHi = 0u,
                OutputLayer = outputLayer,
                ViewRectX = (uint)Math.Max(0, rectX),
                ViewRectY = (uint)Math.Max(0, rectY),
                ViewRectW = (uint)Math.Max(1, rectW),
                ViewRectH = (uint)Math.Max(1, rectH),
                VisibleOffset = visibleOffset,
                VisibleCapacity = Math.Max(1u, visibleCapacity),
                FoveationA = Vector4.Zero,
                FoveationB = Vector4.Zero
            };
        }

        private static GPUViewConstants CreateViewConstants(XRCamera camera)
        {
            Matrix4x4 view = camera.Transform.InverseRenderMatrix;
            Matrix4x4 projection = camera.ProjectionMatrix;
            Matrix4x4 viewProjection = projection * view;
            Vector3 cameraPos = camera.Transform.WorldTranslation;
            Vector3 cameraForward = camera.WorldForward;

            return new GPUViewConstants
            {
                View = view,
                Projection = projection,
                ViewProjection = viewProjection,
                PrevViewProjection = viewProjection,
                CameraPositionAndNear = new Vector4(cameraPos, camera.NearZ),
                CameraForwardAndFar = new Vector4(cameraForward, camera.FarZ)
            };
        }

        public bool TryGetGpuPass(int renderPass, out GPURenderPassCollection gpuPass)
            => _gpuPasses.TryGetValue(renderPass, out gpuPass!);

        public void SwapBuffers()
        {
            using var sample = Engine.Profiler.Start("RenderCommandCollection.SwapBuffers");

            static void Clear(ICollection<RenderCommand> x)
                => x.Clear();
            static void Swap(ICollection<RenderCommand> x)
                => x.ForEach(y => y?.SwapBuffers());
            
            (_updatingPasses, _renderingPasses) = (_renderingPasses, _updatingPasses);

            using (Engine.Profiler.Start("RenderCommandCollection.SwapBuffers.RenderPasses"))
            {
                _renderingPasses.Values.ForEach(Swap);
            }

            using (Engine.Profiler.Start("RenderCommandCollection.SwapBuffers.ClearPasses"))
            {
                _updatingPasses.Values.ForEach(Clear);
            }

            _numCommandsRecentlyAddedToUpdate = 0;
        }

        public IEnumerable<IRenderCommandMesh> EnumerateRenderingMeshCommands()
        {
            foreach (var pass in _renderingPasses.Values)
            {
                foreach (var cmd in pass)
                {
                    if (cmd is IRenderCommandMesh meshCmd)
                        yield return meshCmd;
                }
            }
        }

        public bool TryGetPassMetadata(int passIndex, out RenderPassMetadata metadata)
            => _passMetadata.TryGetValue(passIndex, out metadata!);

        public IReadOnlyDictionary<int, RenderPassMetadata> PassMetadata => _passMetadata;

        public bool ValidatePassMetadata()
        {
            bool valid = true;

            foreach (var (passIndex, passMetadata) in _passMetadata)
            {
                if (!_gpuPasses.ContainsKey(passIndex))
                {
                    Debug.LogWarning($"Render pass metadata references index {passIndex} but no GPU pass exists. Metadata={passMetadata.Name}");
                    valid = false;
                }

                foreach (var usage in passMetadata.ResourceUsages)
                {
                    if (usage.ResourceType is ERenderPassResourceType.ColorAttachment or ERenderPassResourceType.DepthAttachment)
                    {
                        string resourceName = usage.ResourceName;
                        if (!resourceName.StartsWith("fbo::", StringComparison.OrdinalIgnoreCase) &&
                            !resourceName.Equals(RenderGraphResourceNames.OutputRenderTarget, StringComparison.OrdinalIgnoreCase))
                        {
                            Debug.LogWarning($"Pass {passMetadata.Name} references attachment '{resourceName}' that doesn't use fbo:: naming.");
                            valid = false;
                        }
                    }
                }
            }

            return valid;
        }
    }
}
