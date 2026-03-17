using System.Numerics;

namespace XREngine.Rendering.Pipelines.Commands
{
    public sealed class VPRC_SetVariable : ViewportRenderCommand
    {
        public string VariableName { get; set; } = string.Empty;
        public bool ClearVariable { get; set; }
        public bool? BoolValue { get; set; }
        public int? IntValue { get; set; }
        public uint? UIntValue { get; set; }
        public float? FloatValue { get; set; }
        public Vector2? Vector2Value { get; set; }
        public Vector3? Vector3Value { get; set; }
        public Vector4? Vector4Value { get; set; }
        public Matrix4x4? Matrix4Value { get; set; }
        public string? StringValue { get; set; }
        public string? TextureResourceName { get; set; }
        public string? FrameBufferResourceName { get; set; }
        public string? BufferResourceName { get; set; }
        public string? RenderBufferResourceName { get; set; }

        protected override void Execute()
        {
            if (string.IsNullOrWhiteSpace(VariableName))
                return;

            var variables = ActivePipelineInstance.Variables;

            if (ClearVariable)
            {
                variables.Remove(VariableName);
                return;
            }

            if (!string.IsNullOrWhiteSpace(TextureResourceName))
            {
                if (ActivePipelineInstance.TryGetTexture(TextureResourceName, out XRTexture? texture) && texture is not null)
                    variables.SetTexture(VariableName, texture);
                else
                    variables.Set(VariableName, TextureResourceName);
                return;
            }

            if (!string.IsNullOrWhiteSpace(FrameBufferResourceName))
            {
                if (ActivePipelineInstance.TryGetFBO(FrameBufferResourceName, out XRFrameBuffer? frameBuffer) && frameBuffer is not null)
                    variables.SetFrameBuffer(VariableName, frameBuffer);
                else
                    variables.Set(VariableName, FrameBufferResourceName);
                return;
            }

            if (!string.IsNullOrWhiteSpace(BufferResourceName))
            {
                if (ActivePipelineInstance.TryGetBuffer(BufferResourceName, out XRDataBuffer? buffer) && buffer is not null)
                    variables.SetBuffer(VariableName, buffer);
                else
                    variables.Set(VariableName, BufferResourceName);
                return;
            }

            if (!string.IsNullOrWhiteSpace(RenderBufferResourceName))
            {
                if (ActivePipelineInstance.TryGetRenderBuffer(RenderBufferResourceName, out XRRenderBuffer? renderBuffer) && renderBuffer is not null)
                    variables.SetRenderBuffer(VariableName, renderBuffer);
                else
                    variables.Set(VariableName, RenderBufferResourceName);
                return;
            }

            if (Matrix4Value.HasValue)
                variables.Set(VariableName, Matrix4Value.Value);
            else if (Vector4Value.HasValue)
                variables.Set(VariableName, Vector4Value.Value);
            else if (Vector3Value.HasValue)
                variables.Set(VariableName, Vector3Value.Value);
            else if (Vector2Value.HasValue)
                variables.Set(VariableName, Vector2Value.Value);
            else if (FloatValue.HasValue)
                variables.Set(VariableName, FloatValue.Value);
            else if (UIntValue.HasValue)
                variables.Set(VariableName, UIntValue.Value);
            else if (IntValue.HasValue)
                variables.Set(VariableName, IntValue.Value);
            else if (BoolValue.HasValue)
                variables.Set(VariableName, BoolValue.Value);
            else if (StringValue is not null)
                variables.Set(VariableName, StringValue);
        }
    }
}