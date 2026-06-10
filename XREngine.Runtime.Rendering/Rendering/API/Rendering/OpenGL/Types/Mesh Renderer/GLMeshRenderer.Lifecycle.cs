using XREngine.Data.Core;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        public partial class GLMeshRenderer
        {
            /// <summary>Hook mesh/material events and prime state.</summary>
            protected override void LinkData()
            {
                Dbg($"LinkData start (MeshRenderer={MeshRenderer.Name ?? "null"}, Mesh={Mesh?.Name ?? "null"})", "Lifecycle");

                Data.RenderRequested += Render;
                MeshRenderer.PropertyChanged += OnMeshRendererPropertyChanged;
                MeshRenderer.PropertyChanging += OnMeshRendererPropertyChanging;
                SubscribeRendererBuffers(MeshRenderer.Buffers);

                if (Mesh != null)
                    Mesh.DataChanged += OnMeshChanged;

                OnMeshChanged(Mesh);

                Dbg("LinkData complete", "Lifecycle");
            }

            /// <summary>
            /// Detach previously hooked events.
            /// </summary>
            protected override void UnlinkData()
            {
                Dbg("UnlinkData start", "Lifecycle");

                Data.RenderRequested -= Render;
                MeshRenderer.PropertyChanged -= OnMeshRendererPropertyChanged;
                MeshRenderer.PropertyChanging -= OnMeshRendererPropertyChanging;
                SubscribeRendererBuffers(null);

                if (Mesh != null)
                    Mesh.DataChanged -= OnMeshChanged;
                SubscribeMeshBufferCollection(null);

                Dbg("UnlinkData complete", "Lifecycle");
            }

            /// <summary>
            /// Handle property changes on the parent mesh renderer.
            /// </summary>
            /// <param name="sender"></param>
            /// <param name="e"></param>
            private void OnMeshRendererPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
            {
                Dbg($"OnMeshRendererPropertyChanged: {e.PropertyName}", "Lifecycle");
                switch (e.PropertyName)
                {
                    case nameof(XRMeshRenderer.Mesh):
                        if (Mesh != null)
                            Mesh.DataChanged += OnMeshChanged;
                        OnMeshChanged(Mesh);
                        break;
                    case nameof(XRMeshRenderer.Material):
                        OnMaterialChanged();
                        break;
                }
            }

            private void OnMeshRendererPropertyChanging(object? sender, IXRPropertyChangingEventArgs e)
            {
                if (e.PropertyName == nameof(XRMeshRenderer.Mesh) && e.CurrentValue is XRMesh currentMesh)
                {
                    currentMesh.DataChanged -= OnMeshChanged;
                    if (ReferenceEquals(_subscribedMeshBuffers, currentMesh.Buffers))
                        SubscribeMeshBufferCollection(null);
                }
            }

            private void OnMaterialChanged()
            {
                Dbg("OnMaterialChanged", "Lifecycle");
                System.Threading.Interlocked.Increment(ref _materialChangedCount);

                _shadowVariantKey = null;
                _shadowMaterialCache = null;
                Data.ResetVertexShaderSource();
                MeshRenderer.Material?.SyncShaderPipelineProgramForCurrentSettings();

                if (RuntimeEngine.IsRenderThread)
                    RegenerateProgramsAndBuffers();
                else
                    RuntimeEngine.EnqueueMainThreadTask(RegenerateProgramsAndBuffers, "GLMeshRenderer.MaterialChanged");
            }

            private void RegenerateProgramsAndBuffers()
            {
                System.Threading.Interlocked.Increment(ref _programDestructionCount);
                DestroyCombinedProgram();
                DestroySeparablePrograms();

                BuffersBound = false;

                if (!IsGenerated)
                    return;

                if (MeshRenderer.GenerateAsync)
                    Renderer.MeshGenerationQueue.EnqueueGeneration(this);
                else
                {
                    System.Threading.Interlocked.Increment(ref _genCallSiteRegenerate);
                    GenProgramsAndBuffers();
                }
            }

            private void OnMeshChanged(XRMesh? mesh)
            {
                Dbg($"OnMeshChanged -> {mesh?.Name ?? "null"}", "Lifecycle");
                System.Threading.Interlocked.Increment(ref _meshChangedCount);
                SubscribeMeshBuffers(mesh);
                Destroy();

                if (mesh is not null)
                {
                    Renderer.MeshGenerationQueue.ResetRetries(this);
                    Renderer.MeshGenerationQueue.EnqueueGeneration(this);
                }
            }

            private void SubscribeRendererBuffers(XRMesh.BufferCollection? buffers)
            {
                if (ReferenceEquals(_subscribedRendererBuffers, buffers))
                    return;

                if (_subscribedRendererBuffers is not null)
                    _subscribedRendererBuffers.Changed -= Buffers_Changed;

                _subscribedRendererBuffers = buffers;

                if (_subscribedRendererBuffers is not null)
                    _subscribedRendererBuffers.Changed += Buffers_Changed;
            }

            private void SubscribeMeshBuffers(XRMesh? mesh)
                => SubscribeMeshBufferCollection(mesh?.Buffers);

            private void SubscribeMeshBufferCollection(XRMesh.BufferCollection? buffers)
            {
                if (ReferenceEquals(_subscribedMeshBuffers, buffers))
                    return;

                if (_subscribedMeshBuffers is not null)
                    _subscribedMeshBuffers.Changed -= Buffers_Changed;

                _subscribedMeshBuffers = buffers;

                if (_subscribedMeshBuffers is not null)
                    _subscribedMeshBuffers.Changed += Buffers_Changed;
            }

            private void Buffers_Changed()
            {
                System.Threading.Interlocked.Exchange(ref _bufferCollectionsDirty, 1);
                BuffersBound = false;

                if (RuntimeEngine.IsRenderThread)
                {
                    RefreshCollectedBuffersFromCollections();
                    return;
                }

                if (System.Threading.Interlocked.Exchange(ref _bufferCollectionRefreshQueued, 1) == 0)
                {
                    RuntimeRenderingHostServices.Current.EnqueueRenderThreadTask(
                        RefreshCollectedBuffersFromCollections,
                        "GLMeshRenderer.BuffersChanged",
                        RenderThreadJobKind.MeshUpload);
                }
            }

            private void RefreshCollectedBuffersFromCollections()
            {
                System.Threading.Interlocked.Exchange(ref _bufferCollectionRefreshQueued, 0);
                if (System.Threading.Interlocked.Exchange(ref _bufferCollectionsDirty, 0) == 0)
                    return;

                BuffersBound = false;
                if (!IsGenerated)
                    return;

                CollectBuffers();
            }

            /// <summary>
            /// Generate programs/buffers once core GL object is created.
            /// </summary>
            protected internal override void PostGenerated()
            {
                MakeIndexBuffers();

                if (!MeshRenderer.GenerateAsync)
                {
                    System.Threading.Interlocked.Increment(ref _genCallSitePostGenerated);
                    GenProgramsAndBuffers();
                }
            }

            /// <summary>
            /// Dispose any GL objects owned by this renderer.
            /// </summary>
            protected internal override void PostDeleted()
            {
                TriangleIndicesBuffer?.Dispose();
                TriangleIndicesBuffer = null;

                LineIndicesBuffer?.Dispose();
                LineIndicesBuffer = null;

                PointIndicesBuffer?.Dispose();
                PointIndicesBuffer = null;

                _batchedTextSamplesQuery?.Destroy(true);
                _batchedTextSamplesQuery = null;

                DestroySeparablePrograms();
                DestroyCombinedProgram();

                DestroySkinnedBuffers();
                BuffersBound = false;

                foreach (var buffer in _bufferCache)
                    buffer.Value.Destroy();
                _bufferCache = [];
                _ssboBufferCache = [];
            }
        }
    }
}
