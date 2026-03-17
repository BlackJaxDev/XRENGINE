using System.Buffers;
using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// CPU-tests mesh render commands in a pass against the active camera frustum and writes visible source-command indices into a buffer.
    /// </summary>
    public sealed class VPRC_FrustumCullToBuffer : ViewportRenderCommand
    {
        public int RenderPass { get; set; }
        public string? DestinationBufferName { get; set; }
        public bool UploadToGpuBuffer { get; set; } = true;
        public string? VisibleCountVariableName { get; set; }
        public string? CandidateCountVariableName { get; set; }

        protected override void Execute()
        {
            XRRenderPipelineInstance instance = ActivePipelineInstance;
            if (string.IsNullOrWhiteSpace(DestinationBufferName) ||
                !instance.MeshRenderCommands.TryGetRenderingPassCommands(RenderPass, out IReadOnlyCollection<RenderCommand>? commands) ||
                commands is null)
            {
                return;
            }

            XRCamera? camera = instance.RenderState.RenderingCamera ?? instance.RenderState.SceneCamera;
            XRDataBuffer? destinationBuffer = instance.GetBuffer(DestinationBufferName!);
            if (camera is null || destinationBuffer is null)
                return;

            Frustum frustum = camera.WorldFrustum();
            int candidateCapacity = Math.Max(commands.Count, 1);
            uint[] visibleIndices = ArrayPool<uint>.Shared.Rent(candidateCapacity);
            int visibleCount = 0;
            int candidateCount = 0;

            try
            {
                int commandIndex = 0;
                foreach (RenderCommand command in commands)
                {
                    uint sourceIndex = commandIndex < 0 ? 0u : (uint)commandIndex;
                    commandIndex++;

                    if (!command.RenderEnabled || command is not IRenderCommandMesh meshCommand)
                        continue;

                    candidateCount++;
                    if (!TryBuildWorldBounds(meshCommand, out Box worldBounds))
                        continue;

                    if (frustum.ContainsBox(worldBounds) == EContainment.Disjoint)
                        continue;

                    sourceIndex = meshCommand.GPUCommandIndex != uint.MaxValue
                        ? meshCommand.GPUCommandIndex
                        : sourceIndex;
                    visibleIndices[visibleCount++] = sourceIndex;
                }

                destinationBuffer.Allocate<uint>((uint)visibleCount);
                for (uint i = 0; i < visibleCount; ++i)
                    destinationBuffer.SetDataRawAtIndex(i, visibleIndices[i]);

                if (UploadToGpuBuffer)
                    destinationBuffer.PushData();

                if (!string.IsNullOrWhiteSpace(VisibleCountVariableName))
                    instance.Variables.Set(VisibleCountVariableName!, visibleCount);
                if (!string.IsNullOrWhiteSpace(CandidateCountVariableName))
                    instance.Variables.Set(CandidateCountVariableName!, candidateCount);
            }
            finally
            {
                ArrayPool<uint>.Shared.Return(visibleIndices, clearArray: false);
            }
        }

        private static bool TryBuildWorldBounds(IRenderCommandMesh meshCommand, out Box worldBounds)
        {
            worldBounds = default;

            XRMesh? mesh = meshCommand.Mesh?.Mesh;
            if (mesh is null)
                return false;

            AABB localBounds = mesh.Bounds;
            Vector3 size = localBounds.HalfExtents * 2.0f;
            if (size.X < 0.0f || size.Y < 0.0f || size.Z < 0.0f)
                return false;

            worldBounds = new Box(localBounds.Center, size, meshCommand.WorldMatrix);
            return true;
        }
    }
}