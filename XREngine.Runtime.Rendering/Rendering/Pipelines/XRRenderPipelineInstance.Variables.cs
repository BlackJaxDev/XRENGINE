using System.Collections.Generic;
using System.Numerics;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

public sealed partial class XRRenderPipelineInstance
{
    public sealed class PipelineVariableStore
    {
        public Dictionary<string, bool> BoolVariables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> IntVariables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, uint> UIntVariables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, float> FloatVariables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Vector2> Vector2Variables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Vector3> Vector3Variables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Vector4> Vector4Variables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Matrix4x4> Matrix4Variables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> StringVariables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, XRTexture> TextureVariables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, XRFrameBuffer> FrameBufferVariables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, XRDataBuffer> BufferVariables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, XRRenderBuffer> RenderBufferVariables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Clear()
        {
            BoolVariables.Clear();
            IntVariables.Clear();
            UIntVariables.Clear();
            FloatVariables.Clear();
            Vector2Variables.Clear();
            Vector3Variables.Clear();
            Vector4Variables.Clear();
            Matrix4Variables.Clear();
            StringVariables.Clear();
            TextureVariables.Clear();
            FrameBufferVariables.Clear();
            BufferVariables.Clear();
            RenderBufferVariables.Clear();
        }

        public void Remove(string name)
        {
            BoolVariables.Remove(name);
            IntVariables.Remove(name);
            UIntVariables.Remove(name);
            FloatVariables.Remove(name);
            Vector2Variables.Remove(name);
            Vector3Variables.Remove(name);
            Vector4Variables.Remove(name);
            Matrix4Variables.Remove(name);
            StringVariables.Remove(name);
            TextureVariables.Remove(name);
            FrameBufferVariables.Remove(name);
            BufferVariables.Remove(name);
            RenderBufferVariables.Remove(name);
        }

        public void Set(string name, bool value)
        {
            Remove(name);
            BoolVariables[name] = value;
        }

        public void Set(string name, int value)
        {
            Remove(name);
            IntVariables[name] = value;
        }

        public void Set(string name, uint value)
        {
            Remove(name);
            UIntVariables[name] = value;
        }

        public void Set(string name, float value)
        {
            Remove(name);
            FloatVariables[name] = value;
        }

        public void Set(string name, Vector2 value)
        {
            Remove(name);
            Vector2Variables[name] = value;
        }

        public void Set(string name, Vector3 value)
        {
            Remove(name);
            Vector3Variables[name] = value;
        }

        public void Set(string name, Vector4 value)
        {
            Remove(name);
            Vector4Variables[name] = value;
        }

        public void Set(string name, Matrix4x4 value)
        {
            Remove(name);
            Matrix4Variables[name] = value;
        }

        public void Set(string name, string value)
        {
            Remove(name);
            StringVariables[name] = value;
        }

        public void SetTexture(string name, XRTexture texture)
        {
            Remove(name);
            TextureVariables[name] = texture;
        }

        public void SetFrameBuffer(string name, XRFrameBuffer frameBuffer)
        {
            Remove(name);
            FrameBufferVariables[name] = frameBuffer;
        }

        public void SetBuffer(string name, XRDataBuffer buffer)
        {
            Remove(name);
            BufferVariables[name] = buffer;
        }

        public void SetRenderBuffer(string name, XRRenderBuffer renderBuffer)
        {
            Remove(name);
            RenderBufferVariables[name] = renderBuffer;
        }

        public bool TryGet(string name, out bool value)
            => BoolVariables.TryGetValue(name, out value);

        public bool TryGet(string name, out int value)
            => IntVariables.TryGetValue(name, out value);

        public bool TryGet(string name, out uint value)
            => UIntVariables.TryGetValue(name, out value);

        public bool TryGet(string name, out float value)
            => FloatVariables.TryGetValue(name, out value);

        public bool TryGet(string name, out Vector2 value)
            => Vector2Variables.TryGetValue(name, out value);

        public bool TryGet(string name, out Vector3 value)
            => Vector3Variables.TryGetValue(name, out value);

        public bool TryGet(string name, out Vector4 value)
            => Vector4Variables.TryGetValue(name, out value);

        public bool TryGet(string name, out Matrix4x4 value)
            => Matrix4Variables.TryGetValue(name, out value);

        public bool TryGet(string name, out string? value)
            => StringVariables.TryGetValue(name, out value);

        public void Apply(XRRenderProgram program)
        {
            foreach (var pair in BoolVariables)
                program.Uniform(pair.Key, pair.Value);
            foreach (var pair in IntVariables)
                program.Uniform(pair.Key, pair.Value);
            foreach (var pair in UIntVariables)
                program.Uniform(pair.Key, pair.Value);
            foreach (var pair in FloatVariables)
                program.Uniform(pair.Key, pair.Value);
            foreach (var pair in Vector2Variables)
                program.Uniform(pair.Key, pair.Value);
            foreach (var pair in Vector3Variables)
                program.Uniform(pair.Key, pair.Value);
            foreach (var pair in Vector4Variables)
                program.Uniform(pair.Key, pair.Value);
            foreach (var pair in Matrix4Variables)
                program.Uniform(pair.Key, pair.Value);
        }

        public bool TryResolveTexture(RenderResourceRegistry resources, string name, out XRTexture? texture)
        {
            if (TextureVariables.TryGetValue(name, out XRTexture? directTexture))
            {
                texture = directTexture;
                return true;
            }

            if (StringVariables.TryGetValue(name, out string? textureName) &&
                resources.TryGetTexture(textureName, out texture) &&
                texture is not null)
            {
                return true;
            }

            texture = null;
            return false;
        }

        public bool TryResolveFrameBuffer(RenderResourceRegistry resources, string name, out XRFrameBuffer? frameBuffer)
        {
            if (FrameBufferVariables.TryGetValue(name, out XRFrameBuffer? directFrameBuffer))
            {
                frameBuffer = directFrameBuffer;
                return true;
            }

            if (StringVariables.TryGetValue(name, out string? frameBufferName) &&
                resources.TryGetFrameBuffer(frameBufferName, out frameBuffer) &&
                frameBuffer is not null)
            {
                return true;
            }

            frameBuffer = null;
            return false;
        }

        public bool TryResolveBuffer(RenderResourceRegistry resources, string name, out XRDataBuffer? buffer)
        {
            if (BufferVariables.TryGetValue(name, out XRDataBuffer? directBuffer))
            {
                buffer = directBuffer;
                return true;
            }

            if (StringVariables.TryGetValue(name, out string? bufferName) &&
                resources.TryGetBuffer(bufferName, out buffer) &&
                buffer is not null)
            {
                return true;
            }

            buffer = null;
            return false;
        }

        public bool TryResolveRenderBuffer(RenderResourceRegistry resources, string name, out XRRenderBuffer? renderBuffer)
        {
            if (RenderBufferVariables.TryGetValue(name, out XRRenderBuffer? directRenderBuffer))
            {
                renderBuffer = directRenderBuffer;
                return true;
            }

            if (StringVariables.TryGetValue(name, out string? renderBufferName) &&
                resources.TryGetRenderBuffer(renderBufferName, out renderBuffer) &&
                renderBuffer is not null)
            {
                return true;
            }

            renderBuffer = null;
            return false;
        }
    }

    public PipelineVariableStore Variables { get; } = new();
}