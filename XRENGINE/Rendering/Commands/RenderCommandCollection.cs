using Extensions;
using XREngine.Data.Core;
using XREngine.Rendering;
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
        public bool IsShadowPass { get; private set; } = false;
        public void SetRenderPasses(Dictionary<int, IComparer<RenderCommand>?> passIndicesAndSorters, IEnumerable<RenderPassMetadata>? passMetadata = null)
        {
            _updatingPasses = passIndicesAndSorters.ToDictionary(x => x.Key, x => x.Value is null ? [] : (ICollection<RenderCommand>)new SortedSet<RenderCommand>(x.Value));

            _renderingPasses = [];
            _gpuPasses = [];
            _passMetadata = passMetadata?.ToDictionary(m => m.PassIndex) ?? new Dictionary<int, RenderPassMetadata>();
            foreach (KeyValuePair<int, ICollection<RenderCommand>> pass in _updatingPasses)
            {
                _renderingPasses.Add(pass.Key, []);
                var gpuPass = new GPURenderPassCollection(pass.Key);
                gpuPass.SetDebugContext(_ownerPipeline, pass.Key);
                _gpuPasses.Add(pass.Key, gpuPass);

                if (!_passMetadata.ContainsKey(pass.Key))
                    _passMetadata[pass.Key] = new RenderPassMetadata(pass.Key, $"Pass{pass.Key}", RenderGraphPassStage.Graphics);
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

        public void RenderCPU(int renderPass, bool skipGpuCommands = false)
        {
            if (!_renderingPasses.TryGetValue(renderPass, out ICollection<RenderCommand>? list))
            {
                //Debug.Out($"No CPU render pass {renderPass} found.");
                return;
            }

            foreach (var cmd in list)
            {
                if (skipGpuCommands && cmd is IRenderCommandMesh)
                    continue;
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

            gpuPass.Render(scene.GPUCommands);

            if (scene is VisualScene3D visualScene)
            {
                gpuPass.GetVisibleCounts(out uint draws, out uint instances, out _);
                visualScene.RecordGpuVisibility(draws, instances);
            }
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
                    if (usage.ResourceType is RenderPassResourceType.ColorAttachment or RenderPassResourceType.DepthAttachment)
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
