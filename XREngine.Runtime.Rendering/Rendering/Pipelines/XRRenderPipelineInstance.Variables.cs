using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

public sealed partial class XRRenderPipelineInstance
{
    /// <summary>
    /// A store for pipeline variables that can be used to pass data between different stages of the rendering pipeline. 
    /// This allows for flexible communication and data sharing across various rendering commands and processes.
    /// </summary>
    public sealed class PipelineVariableStore
    {
        private readonly Dictionary<string, bool> _boolVariables = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _intVariables = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, uint> _uintVariables = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, float> _floatVariables = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Vector2> _vector2Variables = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Vector3> _vector3Variables = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Vector4> _vector4Variables = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Matrix4x4> _matrix4Variables = new(StringComparer.OrdinalIgnoreCase);
        private long _uniformContentGeneration = 1;

        public IReadOnlyDictionary<string, bool> BoolVariables => _boolVariables;
        public IReadOnlyDictionary<string, int> IntVariables => _intVariables;
        public IReadOnlyDictionary<string, uint> UIntVariables => _uintVariables;
        public IReadOnlyDictionary<string, float> FloatVariables => _floatVariables;
        public IReadOnlyDictionary<string, Vector2> Vector2Variables => _vector2Variables;
        public IReadOnlyDictionary<string, Vector3> Vector3Variables => _vector3Variables;
        public IReadOnlyDictionary<string, Vector4> Vector4Variables => _vector4Variables;
        public IReadOnlyDictionary<string, Matrix4x4> Matrix4Variables => _matrix4Variables;
        public Dictionary<string, string> StringVariables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, XRTexture> TextureVariables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, XRFrameBuffer> FrameBufferVariables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, XRDataBuffer> BufferVariables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, XRRenderBuffer> RenderBufferVariables { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets whether applying this store can emit an unversioned numeric
        /// program binding. Resource lookup dictionaries are excluded because
        /// <see cref="Apply(XRRenderProgram)"/> does not publish them.
        /// </summary>
        public bool HasUniformValues
            => _boolVariables.Count != 0 ||
                _intVariables.Count != 0 ||
                _uintVariables.Count != 0 ||
                _floatVariables.Count != 0 ||
                _vector2Variables.Count != 0 ||
                _vector3Variables.Count != 0 ||
                _vector4Variables.Count != 0 ||
                _matrix4Variables.Count != 0;

        /// <summary>
        /// Monotonic generation for numeric values emitted by
        /// <see cref="Apply(XRRenderProgram)"/>. Same-value writes do not advance it.
        /// </summary>
        public ulong UniformContentGeneration
            => unchecked((ulong)Volatile.Read(ref _uniformContentGeneration));

        private void IncrementUniformContentGeneration()
            => Interlocked.Increment(ref _uniformContentGeneration);

        /// <summary>
        /// Clears all stored variables from the variable store, 
        /// effectively resetting it to an empty state.
        /// </summary>
        public void Clear()
        {
            bool removedUniforms = HasUniformValues;
            _boolVariables.Clear();
            _intVariables.Clear();
            _uintVariables.Clear();
            _floatVariables.Clear();
            _vector2Variables.Clear();
            _vector3Variables.Clear();
            _vector4Variables.Clear();
            _matrix4Variables.Clear();
            StringVariables.Clear();
            TextureVariables.Clear();
            FrameBufferVariables.Clear();
            BufferVariables.Clear();
            RenderBufferVariables.Clear();
            if (removedUniforms)
                IncrementUniformContentGeneration();
        }

        /// <summary>
        /// Removes a variable by name from all variable dictionaries in the store.
        /// This ensures that the variable is completely removed regardless of its type.
        /// </summary>
        /// <param name="name">The name of the variable to remove from the store.</param>
        public void Remove(string name)
        {
            bool removedUniform =
                _boolVariables.Remove(name) |
                _intVariables.Remove(name) |
                _uintVariables.Remove(name) |
                _floatVariables.Remove(name) |
                _vector2Variables.Remove(name) |
                _vector3Variables.Remove(name) |
                _vector4Variables.Remove(name) |
                _matrix4Variables.Remove(name);
            StringVariables.Remove(name);
            TextureVariables.Remove(name);
            FrameBufferVariables.Remove(name);
            BufferVariables.Remove(name);
            RenderBufferVariables.Remove(name);
            if (removedUniform)
                IncrementUniformContentGeneration();
        }

        /// <summary>
        /// Sets a boolean variable in the store, replacing any existing variable with the same name.
        /// If a variable with the same name already exists, it is removed before setting the new value. 
        /// This allows for dynamic updates to the variable store during the rendering process.
        /// </summary>
        /// <param name="name">The name of the variable to set.</param>
        /// <param name="value">The boolean value to set for the variable.</param>
        public void Set(string name, bool value)
        {
            if (_boolVariables.TryGetValue(name, out bool current) &&
                current == value)
            {
                return;
            }
            Remove(name);
            _boolVariables[name] = value;
            IncrementUniformContentGeneration();
        }

        /// <summary>
        /// Sets an integer variable in the store, replacing any existing variable with the same name.
        /// If a variable with the same name already exists, it is removed before setting the new value. 
        /// This allows for dynamic updates to the variable store during the rendering process.
        /// </summary>
        /// <param name="name">The name of the variable to set.</param>
        /// <param name="value">The integer value to set for the variable.</param>
        public void Set(string name, int value)
        {
            if (_intVariables.TryGetValue(name, out int current) &&
                current == value)
            {
                return;
            }
            Remove(name);
            _intVariables[name] = value;
            IncrementUniformContentGeneration();
        }

        /// <summary>
        /// Sets an unsigned integer variable in the store, replacing any existing variable with the same name.
        /// If a variable with the same name already exists, it is removed before setting the new value. 
        /// This allows for dynamic updates to the variable store during the rendering process.
        /// </summary>
        /// <param name="name">The name of the variable to set.</param>
        /// <param name="value">The unsigned integer value to set for the variable.</param>
        public void Set(string name, uint value)
        {
            if (_uintVariables.TryGetValue(name, out uint current) &&
                current == value)
            {
                return;
            }
            Remove(name);
            _uintVariables[name] = value;
            IncrementUniformContentGeneration();
        }

        /// <summary>
        /// Sets a float variable in the store, replacing any existing variable with the same name.
        /// If a variable with the same name already exists, it is removed before setting the new value. 
        /// This allows for dynamic updates to the variable store during the rendering process.
        /// </summary>
        /// <param name="name">The name of the variable to set.</param>
        /// <param name="value">The float value to set for the variable.</param>
        public void Set(string name, float value)
        {
            if (_floatVariables.TryGetValue(name, out float current) &&
                current.Equals(value))
            {
                return;
            }
            Remove(name);
            _floatVariables[name] = value;
            IncrementUniformContentGeneration();
        }

        /// <summary>
        /// Sets a Vector2 variable in the store, replacing any existing variable with the same name.
        /// If a variable with the same name already exists, it is removed before setting the new value. 
        /// This allows for dynamic updates to the variable store during the rendering process.
        /// </summary>
        /// <param name="name">The name of the variable to set.</param>
        /// <param name="value">The Vector2 value to set for the variable.</param>
        public void Set(string name, Vector2 value)
        {
            if (_vector2Variables.TryGetValue(name, out Vector2 current) &&
                current == value)
            {
                return;
            }
            Remove(name);
            _vector2Variables[name] = value;
            IncrementUniformContentGeneration();
        }

        /// <summary>
        /// Sets a Vector3 variable in the store, replacing any existing variable with the same name.
        /// If a variable with the same name already exists, it is removed before setting the new value. 
        /// This allows for dynamic updates to the variable store during the rendering process.
        /// </summary>
        /// <param name="name">The name of the variable to set.</param>
        /// <param name="value">The Vector3 value to set for the variable.</param>
        public void Set(string name, Vector3 value)
        {
            if (_vector3Variables.TryGetValue(name, out Vector3 current) &&
                current == value)
            {
                return;
            }
            Remove(name);
            _vector3Variables[name] = value;
            IncrementUniformContentGeneration();
        }

        /// <summary>
        /// Sets a Vector4 variable in the store, replacing any existing variable with the same name.
        /// If a variable with the same name already exists, it is removed before setting the new value. 
        /// This allows for dynamic updates to the variable store during the rendering process.
        /// </summary>
        /// <param name="name">The name of the variable to set.</param>
        /// <param name="value">The Vector4 value to set for the variable.</param>
        public void Set(string name, Vector4 value)
        {
            if (_vector4Variables.TryGetValue(name, out Vector4 current) &&
                current == value)
            {
                return;
            }
            Remove(name);
            _vector4Variables[name] = value;
            IncrementUniformContentGeneration();
        }

        /// <summary>
        /// Sets a Matrix4x4 variable in the store, replacing any existing variable with the same name.
        /// If a variable with the same name already exists, it is removed before setting the new value. 
        /// This allows for dynamic updates to the variable store during the rendering process.
        /// </summary>
        /// <param name="name">The name of the variable to set.</param>
        /// <param name="value">The Matrix4x4 value to set for the variable.</param>
        public void Set(string name, Matrix4x4 value)
        {
            if (_matrix4Variables.TryGetValue(
                    name,
                    out Matrix4x4 current) &&
                current == value)
            {
                return;
            }
            Remove(name);
            _matrix4Variables[name] = value;
            IncrementUniformContentGeneration();
        }

        /// <summary>
        /// Sets a string variable in the store, replacing any existing variable with the same name.
        /// If a variable with the same name already exists, it is removed before setting the new value. 
        /// This allows for dynamic updates to the variable store during the rendering process.
        /// </summary>
        /// <param name="name">The name of the variable to set.</param>
        /// <param name="value">The string value to set for the variable.</param>
        public void Set(string name, string value)
        {
            Remove(name);
            StringVariables[name] = value;
        }

        /// <summary>
        /// Sets a texture variable in the store, replacing any existing variable with the same name.
        /// If a variable with the same name already exists, it is removed before setting the new value. 
        /// This allows for dynamic updates to the variable store during the rendering process.
        /// </summary>
        /// <param name="name">The name of the variable to set.</param>
        /// <param name="texture">The texture value to set for the variable.</param>
        public void SetTexture(string name, XRTexture texture)
        {
            Remove(name);
            TextureVariables[name] = texture;
        }

        /// <summary>
        /// Sets a framebuffer variable in the store, replacing any existing variable with the same name.
        /// If a variable with the same name already exists, it is removed before setting the new value. 
        /// This allows for dynamic updates to the variable store during the rendering process.
        /// </summary>
        /// <param name="name">The name of the variable to set.</param>
        /// <param name="frameBuffer">The framebuffer value to set for the variable.</param>
        public void SetFrameBuffer(string name, XRFrameBuffer frameBuffer)
        {
            Remove(name);
            FrameBufferVariables[name] = frameBuffer;
        }

        /// <summary>
        /// Sets a data buffer variable in the store, replacing any existing variable with the same name.
        /// If a variable with the same name already exists, it is removed before setting the new value. 
        /// This allows for dynamic updates to the variable store during the rendering process.
        /// </summary>
        /// <param name="name">The name of the variable to set.</param>
        /// <param name="buffer">The data buffer value to set for the variable.</param>
        public void SetBuffer(string name, XRDataBuffer buffer)
        {
            Remove(name);
            BufferVariables[name] = buffer;
        }

        /// <summary>
        /// Sets a render buffer variable in the store, replacing any existing variable with the same name.
        /// If a variable with the same name already exists, it is removed before setting the new value. 
        /// This allows for dynamic updates to the variable store during the rendering process.
        /// </summary>
        /// <param name="name">The name of the variable to set.</param>
        /// <param name="renderBuffer">The render buffer value to set for the variable.</param>
        public void SetRenderBuffer(string name, XRRenderBuffer renderBuffer)
        {
            Remove(name);
            RenderBufferVariables[name] = renderBuffer;
        }

        /// <summary>
        /// Attempts to retrieve a boolean variable by name from the variable store.
        /// If the variable exists, it is returned via the out parameter and the method returns true; 
        /// otherwise, it returns false and the out parameter is set to the default value of bool.
        /// This allows for flexible retrieval of boolean data used in the rendering pipeline.
        /// </summary>
        /// <param name="name">The name of the boolean variable to retrieve.</param>
        /// <param name="value">When this method returns, contains the boolean value associated with the specified name, if the name is found; otherwise, the default value of bool.</param>
        /// <returns>True if the variable exists; otherwise, false.</returns>
        public bool TryGet(string name, out bool value)
            => _boolVariables.TryGetValue(name, out value);

        /// <summary>
        /// Attempts to retrieve an integer variable by name from the variable store.
        /// If the variable exists, it is returned via the out parameter and the method returns true; 
        /// otherwise, it returns false and the out parameter is set to the default value of int.
        /// This allows for flexible retrieval of integer data used in the rendering pipeline.
        /// </summary>
        /// <param name="name">The name of the integer variable to retrieve.</param>
        /// <param name="value">When this method returns, contains the integer value associated with the specified name, if the name is found; otherwise, the default value of int.</param>
        /// <returns>True if the variable exists; otherwise, false.</returns>
        public bool TryGet(string name, out int value)
            => _intVariables.TryGetValue(name, out value);

        /// <summary>
        /// Attempts to retrieve an unsigned integer variable by name from the variable store.
        /// If the variable exists, it is returned via the out parameter and the method returns true; 
        /// otherwise, it returns false and the out parameter is set to the default value of uint.
        /// This allows for flexible retrieval of unsigned integer data used in the rendering pipeline.
        /// </summary>
        /// <param name="name">The name of the unsigned integer variable to retrieve.</param>
        /// <param name="value">When this method returns, contains the unsigned integer value associated with the specified name, if the name is found; otherwise, the default value of uint.</param>
        /// <returns>True if the variable exists; otherwise, false.</returns>
        public bool TryGet(string name, out uint value)
            => _uintVariables.TryGetValue(name, out value);

        /// <summary>
        /// Attempts to retrieve a float variable by name from the variable store.
        /// If the variable exists, it is returned via the out parameter and the method returns true; 
        /// otherwise, it returns false and the out parameter is set to the default value of float.
        /// This allows for flexible retrieval of float data used in the rendering pipeline.
        /// </summary>
        /// <param name="name">The name of the float variable to retrieve.</param>
        /// <param name="value">When this method returns, contains the float value associated with the specified name, if the name is found; otherwise, the default value of float.</param>
        /// <returns>True if the variable exists; otherwise, false.</returns>
        public bool TryGet(string name, out float value)
            => _floatVariables.TryGetValue(name, out value);

        /// <summary>
        /// Attempts to retrieve a Vector2 variable by name from the variable store.
        /// If the variable exists, it is returned via the out parameter and the method returns true;
        /// otherwise, it returns false and the out parameter is set to the default value of Vector2.
        /// This allows for flexible retrieval of vector data used in the rendering pipeline.
        /// </summary>
        /// <param name="name">The name of the Vector2 variable to retrieve.</param>
        /// <param name="value">When this method returns, contains the Vector2 value associated with the specified name, if the name is found; otherwise, the default value of Vector2.</param>
        /// <returns>True if the variable exists; otherwise, false.</returns>
        public bool TryGet(string name, out Vector2 value)
            => _vector2Variables.TryGetValue(name, out value);

        /// <summary>
        /// Attempts to retrieve a Vector3 variable by name from the variable store.
        /// If the variable exists, it is returned via the out parameter and the method returns true;
        /// otherwise, it returns false and the out parameter is set to the default value of Vector3.
        /// This allows for flexible retrieval of vector data used in the rendering pipeline.
        /// </summary>
        /// <param name="name">The name of the Vector3 variable to retrieve.</param>
        /// <param name="value">When this method returns, contains the Vector3 value associated with the specified name, if the name is found; otherwise, the default value of Vector3.</param>
        /// <returns>True if the variable exists; otherwise, false.</returns>
        public bool TryGet(string name, out Vector3 value)
            => _vector3Variables.TryGetValue(name, out value);

        /// <summary>
        /// Attempts to retrieve a Vector4 variable by name from the variable store.
        /// If the variable exists, it is returned via the out parameter and the method returns true; 
        /// otherwise, it returns false and the out parameter is set to the default value of Vector4.
        /// This allows for flexible retrieval of vector data used in the rendering pipeline.
        /// </summary>
        /// <param name="name">The name of the Vector4 variable to retrieve.</param>
        /// <param name="value">When this method returns, contains the Vector4 value associated with the specified name, if the name is found; otherwise, the default value of Vector4.</param>
        /// <returns>True if the variable exists; otherwise, false.</returns>
        public bool TryGet(string name, out Vector4 value)
            => _vector4Variables.TryGetValue(name, out value);

        /// <summary>
        /// Attempts to retrieve a Matrix4x4 variable by name from the variable store. 
        /// If the variable exists, it is returned via the out parameter and the method returns true; 
        /// otherwise, it returns false and the out parameter is set to the default value of Matrix4x4. 
        /// This allows for flexible retrieval of matrix data used in the rendering pipeline.
        /// </summary>
        /// <param name="name">The name of the Matrix4x4 variable to retrieve.</param>
        /// <param name="value">When this method returns, contains the Matrix4x4 value associated with the specified name, if the name is found; otherwise, the default value of Matrix4x4.</param>
        /// <returns>True if the variable exists; otherwise, false.</returns>
        public bool TryGet(string name, out Matrix4x4 value)
            => _matrix4Variables.TryGetValue(name, out value);

        /// <summary>
        /// Attempts to retrieve a string variable by name from the variable store.
        /// If the variable exists, it is returned via the out parameter and the method returns true; 
        /// otherwise, it returns false and the out parameter is set to null.
        /// This allows for flexible retrieval of string data used in the rendering pipeline.
        /// </summary>
        /// <param name="name">The name of the string variable to retrieve.</param>
        /// <param name="value">When this method returns, contains the string value associated with the specified name, if the name is found; otherwise, null.</param>
        /// <returns>True if the variable exists; otherwise, false.</returns>
        public bool TryGet(string name, out string? value)
            => StringVariables.TryGetValue(name, out value);

        /// <summary>
        /// Applies the stored variables to the given XRRenderProgram, 
        /// setting the appropriate uniforms for each variable type. 
        /// This allows for the transfer of data from the variable store 
        /// to the rendering program for use in shaders.
        /// </summary>
        public void Apply(XRRenderProgram program)
        {
            foreach (var pair in _boolVariables)
                program.Uniform(pair.Key, pair.Value);
            foreach (var pair in _intVariables)
                program.Uniform(pair.Key, pair.Value);
            foreach (var pair in _uintVariables)
                program.Uniform(pair.Key, pair.Value);
            foreach (var pair in _floatVariables)
                program.Uniform(pair.Key, pair.Value);
            foreach (var pair in _vector2Variables)
                program.Uniform(pair.Key, pair.Value);
            foreach (var pair in _vector3Variables)
                program.Uniform(pair.Key, pair.Value);
            foreach (var pair in _vector4Variables)
                program.Uniform(pair.Key, pair.Value);
            foreach (var pair in _matrix4Variables)
                program.Uniform(pair.Key, pair.Value);
        }

        /// <summary>
        /// Attempts to resolve a texture by name, first checking the direct variable store and then falling back to the resource registry if necessary. 
        /// This allows for flexible retrieval of textures used in the rendering pipeline.
        /// </summary>
        /// <param name="resources">The resource registry to fall back on if the texture is not found in the direct variable store.</param>
        /// <param name="name">The name of the texture to resolve.</param>
        /// <param name="texture">The resolved texture if found; otherwise, null.</param>
        /// <returns>True if the texture was successfully resolved; otherwise, false.</returns>
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
                return true;

            texture = null;
            return false;
        }

        /// <summary>
        /// Attempts to resolve a framebuffer by name, first checking the direct variable store and then falling back to the resource registry if necessary. 
        /// This allows for flexible retrieval of framebuffers used in the rendering pipeline.
        /// If the framebuffer is found, it is returned via the out parameter and the method returns true; 
        /// otherwise, it returns false and the out parameter is set to null.
        /// </summary>
        /// <param name="resources">The resource registry to fall back on if the framebuffer is not found in the direct variable store.</param>
        /// <param name="name">The name of the framebuffer to resolve.</param>
        /// <param name="frameBuffer">The resolved framebuffer if found; otherwise, null.</param>
        /// <returns>True if the framebuffer was successfully resolved; otherwise, false.</returns>
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
                return true;

            frameBuffer = null;
            return false;
        }

        /// <summary>
        /// Attempts to resolve a data buffer by name, first checking the direct variable store and then falling back to the resource registry if necessary. 
        /// This allows for flexible retrieval of data buffers used in the rendering pipeline.
        /// If the data buffer is found, it is returned via the out parameter and the method returns true; 
        /// otherwise, it returns false and the out parameter is set to null.
        /// </summary>
        /// <param name="resources">The resource registry to fall back on if the buffer is not found in the direct variable store.</param>
        /// <param name="name">The name of the data buffer to resolve.</param>
        /// <param name="buffer">The resolved data buffer if found; otherwise, null.</param>
        /// <returns>True if the data buffer was successfully resolved; otherwise, false.</returns>
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
                return true;

            buffer = null;
            return false;
        }

        /// <summary>
        /// Attempts to resolve a render buffer by name, first checking the direct variable store and then falling back to the resource registry if necessary. This allows for flexible retrieval of render buffers used in the rendering pipeline.
        /// If the render buffer is found, it is returned via the out parameter and the method returns true; otherwise, it returns false and the out parameter is set to null.
        /// </summary>
        /// <param name="resources"></param>
        /// <param name="name"></param>
        /// <param name="renderBuffer"></param>
        /// <returns></returns>
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
                return true;

            renderBuffer = null;
            return false;
        }
    }

    /// <summary>
    /// A store for pipeline variables that can be used to pass data between different stages of the rendering pipeline. 
    /// This allows for flexible communication and data sharing across various rendering commands and processes.
    /// </summary>
    public PipelineVariableStore Variables { get; } = new();
}
