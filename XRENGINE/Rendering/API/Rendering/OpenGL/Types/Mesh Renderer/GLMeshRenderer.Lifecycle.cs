using System.Threading.Tasks;
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

                if (Mesh != null)
                    Mesh.DataChanged -= OnMeshChanged;

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
                        OnMeshChanged(Mesh);
                        break;
                }
            }

            private void OnMeshRendererPropertyChanging(object? sender, IXRPropertyChangingEventArgs e)
            {
                
            }

            private void OnMeshChanged(XRMesh? mesh)
            {
                Dbg($"OnMeshChanged -> {mesh?.Name ?? "null"}", "Lifecycle");
                Destroy();
            }

            /// <summary>
            /// Generate programs/buffers once core GL object is created.
            /// </summary>
            protected internal override void PostGenerated()
            {
                if (MeshRenderer.GenerateAsync)
                    Task.Run(GenProgramsAndBuffers);
                else
                    GenProgramsAndBuffers();
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

                _pipeline?.Destroy();
                _pipeline = null;

                _separatedVertexProgram?.Destroy();
                _separatedVertexProgram = null;

                _combinedProgram?.Destroy();
                _combinedProgram = null;

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
