﻿using Extensions;
using XREngine.Data.Core;
using XREngine.Rendering;

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
        public void SetRenderPasses(Dictionary<int, IComparer<RenderCommand>?> passIndicesAndSorters)
        {
            _updatingPasses = passIndicesAndSorters.ToDictionary(x => x.Key, x => x.Value is null ? [] : (ICollection<RenderCommand>)new SortedSet<RenderCommand>(x.Value));

            _renderingPasses = [];
            _gpuPasses = [];
            foreach (KeyValuePair<int, ICollection<RenderCommand>> pass in _updatingPasses)
            {
                _renderingPasses.Add(pass.Key, []);
                var gpuPass = new GPURenderPassCollection(pass.Key);
                gpuPass.SetDebugContext(_ownerPipeline, pass.Key);
                _gpuPasses.Add(pass.Key, gpuPass);
            }
        }

        private int _numCommandsRecentlyAddedToUpdate = 0;

        private Dictionary<int, ICollection<RenderCommand>> _updatingPasses = [];
        private Dictionary<int, ICollection<RenderCommand>> _renderingPasses = [];
        private Dictionary<int, GPURenderPassCollection> _gpuPasses = [];
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

        public void RenderCPU(int renderPass)
        {
            if (!_renderingPasses.TryGetValue(renderPass, out ICollection<RenderCommand>? list))
            {
                //Debug.Out($"No CPU render pass {renderPass} found.");
                return;
            }
            list.ForEach(x => x?.Render());
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
        }

        public void SwapBuffers()
        {
            static void Clear(ICollection<RenderCommand> x)
                => x.Clear();
            static void Swap(ICollection<RenderCommand> x)
                => x.ForEach(y => y?.SwapBuffers());
            
            (_updatingPasses, _renderingPasses) = (_renderingPasses, _updatingPasses);

            _renderingPasses.Values.ForEach(Swap);
            _updatingPasses.Values.ForEach(Clear);

            _numCommandsRecentlyAddedToUpdate = 0;
        }
    }
}
