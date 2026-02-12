using Extensions;
using Silk.NET.OpenGL;
using System.Collections;
using System.Collections.Concurrent;
using System.Numerics;
using System.Linq;
using System.Text;
using System.Threading;
using XREngine.Data.Vectors;
using XREngine;
using static XREngine.Rendering.XRRenderProgram;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        public delegate void DelCompile(bool compiledSuccessfully, string? compileInfo);
        public class GLRenderProgram(OpenGLRenderer renderer, XRRenderProgram data) : GLObject<XRRenderProgram>(renderer, data), IEnumerable<GLShader>
        {
            private bool _isLinked = false;
            public bool IsLinked
            {
                get => _isLinked;
                private set => SetField(ref _isLinked, value);
            }

            public override EGLObjectType Type => EGLObjectType.Program;

            private readonly ConcurrentDictionary<string, int>
                _uniformCache = new(),
                _attribCache = new();

            private readonly ConcurrentDictionary<int, string> _locationNameCache = new();
            private readonly ConcurrentDictionary<string, UniformInfo> _uniformMetadata = new();
            private readonly ConcurrentDictionary<string, byte> _loggedUniformMismatches = new();

            private int _uniformBindingAttempts;
            private int _uniformBindings;
            private int _samplerBindingAttempts;
            private int _samplerBindings;
            private readonly ConcurrentDictionary<string, byte> _loggedEmptyBindingBatches = new();

            private readonly ConcurrentBag<string> _failedAttributes = [];
            private readonly ConcurrentDictionary<string, byte> _failedUniforms = new();

            private readonly record struct UniformInfo(GLEnum Type, int Size);

            protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
            {
                base.OnPropertyChanged(propName, prev, field);
                switch (propName)
                {
                    case nameof(Data.Shaders):
                        if (IsLinked)
                            Relink();
                        break;
                }
            }

            /// <summary>
            /// If the program has been generated and linked successfully,
            /// this will return the location of the uniform with the given name.
            /// Cached for performance and thread-safe.
            /// </summary>
            /// <param name="name"></param>
            /// <returns></returns>
            public int GetUniformLocation(string name)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.GetUniformLocation");

                if (!IsLinked)
                    return -1;

                if (_uniformCache.TryGetValue(name, out int value))
                    return value;

                if (!GetUniform(name, out value))
                {
                    _uniformCache.TryAdd(name, -1);
                    return -1;
                }

                _uniformCache.TryAdd(name, value);
                if (value >= 0)
                    _locationNameCache[value] = name;
                return value;
            }
            private bool GetUniform(string name, out int location)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.GetUniform");

                bool failed = _failedUniforms.ContainsKey(name);
                if (failed)
                {
                    location = -1;
                    return false;
                }
                location = Api.GetUniformLocation(BindingId, name);
                if (location < 0)
                {
                    if (!failed)
                        _failedUniforms.TryAdd(name, 0);
                    return false;
                }
                return true;
            }
            /// <summary>
            /// If the program has been generated and linked successfully,
            /// this will return the location of the attribute with the given name.
            /// Cached for performance and thread-safe.
            /// <param name="name"></param>
            /// <returns></returns>
            public int GetAttributeLocation(string name)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.GetAttributeLocation");

                if (!IsLinked)
                    return -1;

                if (_attribCache.TryGetValue(name, out int value))
                    return value;

                if (!GetAttribute(name, out value))
                    return -1;

                _attribCache.TryAdd(name, value);
                return value;
            }
            private bool GetAttribute(string name, out int location)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.GetAttribute");

                bool failed = _failedAttributes.Contains(name);
                if (failed)
                {
                    location = -1;
                    return false;
                }
                location = Api.GetAttribLocation(BindingId, name);
                if (location < 0)
                {
                    if (!failed)
                    {
                        _failedAttributes.Add(name);
                        //Debug.LogWarning($"Attribute {name} not found in OpenGL program.");
                    }
                    return false;
                }
                return true;
            }

            private readonly ConcurrentDictionary<XRShader, GLShader> _shaderCache = [];

            private void ShaderRemoved(XRShader item)
            {
                if (!_shaderCache.TryRemove(item, out var shader) || shader is null)
                    return;

                shader.Destroy();
                ShaderUncached(shader);
            }

            private void ShaderAdded(XRShader item)
            {
                _shaderCache.TryAdd(item, GetAndGenerate(item));
            }
            private GLShader GetAndGenerate(XRShader data)
            {
                GLShader shader = Renderer.GenericToAPI<GLShader>(data)!;
                //Engine.EnqueueMainThreadTask(shader.Generate);
                ShaderCached(shader);
                return shader;
            }

            private void ShaderCached(GLShader shader)
            {
                shader.ActivePrograms.Add(this);
                shader.SourceChanged += Value_SourceChanged;
            }

            private void ShaderUncached(GLShader shader)
            {
                shader.ActivePrograms.Remove(this);
                shader.SourceChanged -= Value_SourceChanged;
            }

            protected override void LinkData()
            {
                //data.UniformLocationRequested = GetUniformLocation;

                Data.UniformSetVector2Requested += Uniform;
                Data.UniformSetVector3Requested += Uniform;
                Data.UniformSetVector4Requested += Uniform;
                Data.UniformSetQuaternionRequested += Uniform;
                Data.UniformSetIntRequested += Uniform;
                Data.UniformSetFloatRequested += Uniform;
                Data.UniformSetUIntRequested += Uniform;
                Data.UniformSetDoubleRequested += Uniform;
                Data.UniformSetMatrix4x4Requested += Uniform;

                Data.UniformSetVector2ArrayRequested += Uniform;
                Data.UniformSetVector3ArrayRequested += Uniform;
                Data.UniformSetVector4ArrayRequested += Uniform;
                Data.UniformSetQuaternionArrayRequested += Uniform;
                Data.UniformSetIntArrayRequested += Uniform;
                Data.UniformSetFloatArrayRequested += Uniform;
                Data.UniformSetFloatSpanRequested += Uniform;
                Data.UniformSetUIntArrayRequested += Uniform;
                Data.UniformSetDoubleArrayRequested += Uniform;
                Data.UniformSetMatrix4x4ArrayRequested += Uniform;

                Data.UniformSetIVector2Requested += Uniform;
                Data.UniformSetIVector3Requested += Uniform;
                Data.UniformSetIVector4Requested += Uniform;
                Data.UniformSetIVector2ArrayRequested += Uniform;
                Data.UniformSetIVector3ArrayRequested += Uniform;
                Data.UniformSetIVector4ArrayRequested += Uniform;

                Data.UniformSetBoolRequested += Uniform;
                Data.UniformSetBoolArrayRequested += Uniform;
                
                Data.UniformSetBoolVector2Requested += Uniform;
                Data.UniformSetBoolVector3Requested += Uniform;
                Data.UniformSetBoolVector4Requested += Uniform;

                Data.UniformSetBoolVector2ArrayRequested += Uniform;
                Data.UniformSetBoolVector3ArrayRequested += Uniform;
                Data.UniformSetBoolVector4ArrayRequested += Uniform;

                Data.SamplerRequested += Sampler;
                Data.SamplerRequestedByLocation += Sampler;
                Data.BindImageTextureRequested += BindImageTexture;
                Data.DispatchComputeRequested += DispatchCompute;
                Data.BindBufferRequested += BindBuffer;

                Data.LinkRequested += LinkRequested;
                Data.UseRequested += UseRequested;

                foreach (XRShader shader in Data.Shaders)
                    ShaderAdded(shader);
                Data.Shaders.PostAnythingAdded += ShaderAdded;
                Data.Shaders.PostAnythingRemoved += ShaderRemoved;
            }

            private void UseRequested(XRRenderProgram program)
            {
                if (Engine.InvokeOnMainThread(() => UseRequested(program), "GLRenderProgram.UseRequested"))
                    return;

                if (!IsLinked)
                {
                    //Debug.LogWarning("Cannot use program, it is not linked.");
                    return;
                }

                Api.UseProgram(BindingId);
            }

            private void LinkRequested(XRRenderProgram program)
            {
                if (Engine.InvokeOnMainThread(() => LinkRequested(program), "GLRenderProgram.LinkRequested"))
                    return;

                if (!Link())
                {
                    //Debug.LogWarning($"Failed to link program {Data.Name} with hash {Hash}.");
                }
            }

            private void BindBuffer(uint index, XRDataBuffer buffer)
            {
                var glObj = Renderer.GetOrCreateAPIRenderObject(buffer);
                if (glObj is not GLDataBuffer glBuf)
                    return;

                Api.BindBufferBase(GLEnum.ShaderStorageBuffer, index, glBuf.BindingId);
            }

            private void BindImageTexture(uint unit, XRTexture texture, int level, bool layered, int layer, EImageAccess access, EImageFormat format)
            {
                var glObj = Renderer.GetOrCreateAPIRenderObject(texture);
                if (glObj is not IGLTexture glTex)
                    return;
                Api.BindImageTexture(unit, glTex.BindingId, level, layered, layer, ToGLEnum(access), ToGLEnum(format));
            }

            private static GLEnum ToGLEnum(EImageFormat format) => format switch
            {
                EImageFormat.R8 => GLEnum.R8,
                EImageFormat.R16 => GLEnum.R16,
                EImageFormat.R16F => GLEnum.R16f,
                EImageFormat.R32F => GLEnum.R32f,
                EImageFormat.RG8 => GLEnum.RG8,
                EImageFormat.RG16 => GLEnum.RG16,
                EImageFormat.RG16F => GLEnum.RG16f,
                EImageFormat.RG32F => GLEnum.RG32f,
                EImageFormat.RGB8 => GLEnum.Rgb8,
                EImageFormat.RGB16 => GLEnum.Rgb16,
                EImageFormat.RGB16F => GLEnum.Rgb16f,
                EImageFormat.RGB32F => GLEnum.Rgb32f,
                EImageFormat.RGBA8 => GLEnum.Rgba8,
                EImageFormat.RGBA16 => GLEnum.Rgba16,
                EImageFormat.RGBA16F => GLEnum.Rgba16f,
                EImageFormat.RGBA32F => GLEnum.Rgba32f,
                EImageFormat.R8I => GLEnum.R8i,
                EImageFormat.R8UI => GLEnum.R8ui,
                EImageFormat.R16I => GLEnum.R16i,
                EImageFormat.R16UI => GLEnum.R16ui,
                EImageFormat.R32I => GLEnum.R32i,
                EImageFormat.R32UI => GLEnum.R32ui,
                EImageFormat.RG8I => GLEnum.RG8i,
                EImageFormat.RG8UI => GLEnum.RG8ui,
                EImageFormat.RG16I => GLEnum.RG16i,
                EImageFormat.RG16UI => GLEnum.RG16ui,
                EImageFormat.RG32I => GLEnum.RG32i,
                EImageFormat.RG32UI => GLEnum.RG32ui,
                EImageFormat.RGB8I => GLEnum.Rgb8i,
                EImageFormat.RGB8UI => GLEnum.Rgb8ui,
                EImageFormat.RGB16I => GLEnum.Rgb16i,
                EImageFormat.RGB16UI => GLEnum.Rgb16ui,
                EImageFormat.RGB32I => GLEnum.Rgb32i,
                EImageFormat.RGB32UI => GLEnum.Rgb32ui,
                EImageFormat.RGBA8I => GLEnum.Rgba8i,
                EImageFormat.RGBA8UI => GLEnum.Rgba8ui,
                EImageFormat.RGBA16I => GLEnum.Rgba16i,
                EImageFormat.RGBA16UI => GLEnum.Rgba16ui,
                EImageFormat.RGBA32I => GLEnum.Rgba32i,
                EImageFormat.RGBA32UI => GLEnum.Rgba32ui,
                _ => GLEnum.Rgba32f,
            };

            private static GLEnum ToGLEnum(EImageAccess access) => access switch
            {
                EImageAccess.ReadOnly => GLEnum.ReadOnly,
                EImageAccess.WriteOnly => GLEnum.WriteOnly,
                EImageAccess.ReadWrite => GLEnum.ReadWrite,
                _ => GLEnum.ReadWrite,
            };

            private void DispatchCompute(
                uint x,
                uint y,
                uint z,
                IEnumerable<(uint unit, XRTexture texture, int level, int? layer, EImageAccess access, EImageFormat format)>? textures = null)
            {
                if (!IsLinked)
                {
                    if (Data.LinkReady)
                    {
                        if (!Link())
                        {
                            //Debug.LogWarning($"Failed to link program {Data.Name} with hash {Hash}.");
                            return;
                        }
                    }
                    else
                    {
                        Debug.OpenGLWarning("Cannot dispatch compute shader, program is not linked.");
                        return;
                    }
                }
                Api.UseProgram(BindingId);
                if (textures is not null)
                    foreach (var (unit, texture, level, layer, access, format) in textures)
                        BindImageTexture(unit, texture, level, layer.HasValue, layer ?? 0, access, format);
                Api.DispatchCompute(x, y, z);
            }

            protected override void UnlinkData()
            {
                Data.UniformSetVector2Requested -= Uniform;
                Data.UniformSetVector3Requested -= Uniform;
                Data.UniformSetVector4Requested -= Uniform;
                Data.UniformSetQuaternionRequested -= Uniform;
                Data.UniformSetIntRequested -= Uniform;
                Data.UniformSetFloatRequested -= Uniform;
                Data.UniformSetUIntRequested -= Uniform;
                Data.UniformSetDoubleRequested -= Uniform;
                Data.UniformSetMatrix4x4Requested -= Uniform;

                Data.UniformSetVector2ArrayRequested -= Uniform;
                Data.UniformSetVector3ArrayRequested -= Uniform;
                Data.UniformSetVector4ArrayRequested -= Uniform;
                Data.UniformSetQuaternionArrayRequested -= Uniform;
                Data.UniformSetIntArrayRequested -= Uniform;
                Data.UniformSetFloatArrayRequested -= Uniform;
                Data.UniformSetFloatSpanRequested -= Uniform;
                Data.UniformSetUIntArrayRequested -= Uniform;
                Data.UniformSetDoubleArrayRequested -= Uniform;
                Data.UniformSetMatrix4x4ArrayRequested -= Uniform;

                Data.UniformSetIVector2Requested -= Uniform;
                Data.UniformSetIVector3Requested -= Uniform;
                Data.UniformSetIVector4Requested -= Uniform;
                Data.UniformSetIVector2ArrayRequested -= Uniform;
                Data.UniformSetIVector3ArrayRequested -= Uniform;
                Data.UniformSetIVector4ArrayRequested -= Uniform;

                //Data.UniformSetUVector2Requested -= Uniform;
                //Data.UniformSetUVector3Requested -= Uniform;
                //Data.UniformSetUVector4Requested -= Uniform;

                Data.UniformSetBoolRequested -= Uniform;
                Data.UniformSetBoolArrayRequested -= Uniform;

                Data.UniformSetBoolVector2Requested -= Uniform;
                Data.UniformSetBoolVector3Requested -= Uniform;
                Data.UniformSetBoolVector4Requested -= Uniform;

                Data.UniformSetBoolVector2ArrayRequested -= Uniform;
                Data.UniformSetBoolVector3ArrayRequested -= Uniform;
                Data.UniformSetBoolVector4ArrayRequested -= Uniform;

                Data.SamplerRequested -= Sampler;
                Data.SamplerRequestedByLocation -= Sampler;
                Data.BindImageTextureRequested -= BindImageTexture;
                Data.DispatchComputeRequested -= DispatchCompute;
                Data.BindBufferRequested -= BindBuffer;

                Data.LinkRequested -= LinkRequested;
                Data.UseRequested -= UseRequested;

                Data.Shaders.PostAnythingAdded -= ShaderAdded;
                Data.Shaders.PostAnythingRemoved -= ShaderRemoved;
                foreach (XRShader shader in Data.Shaders)
                    ShaderRemoved(shader);
            }

            public bool LinkReady => Data.LinkReady;

            private void Reset()
            {
                IsLinked = false;
                _attribCache.Clear();
                _uniformCache.Clear();
                _failedAttributes.Clear();
                _failedUniforms.Clear();
                _locationNameCache.Clear();
                _uniformMetadata.Clear();
                _loggedUniformMismatches.Clear();
                _loggedEmptyBindingBatches.Clear();
                _uniformBindingAttempts = 0;
                _uniformBindings = 0;
                _samplerBindingAttempts = 0;
                _samplerBindings = 0;
            }

            private void CacheActiveUniforms()
            {
                if (!IsLinked)
                    return;

                _uniformMetadata.Clear();

                Api.GetProgram(BindingId, GLEnum.ActiveUniforms, out int uniformCount);
                if (uniformCount <= 0)
                    return;

                Api.GetProgram(BindingId, GLEnum.ActiveUniformMaxLength, out int maxLength);
                if (maxLength <= 0)
                    maxLength = 256;

                byte[] nameBuffer = new byte[maxLength];

                fixed (byte* namePtr = nameBuffer)
                {
                    for (uint index = 0; index < (uint)uniformCount; index++)
                    {
                        uint length;
                        int size;
                        GLEnum type;

                        Api.GetActiveUniform(BindingId, index, (uint)nameBuffer.Length, out length, out size, out type, namePtr);
                        if (length == 0 || length > nameBuffer.Length)
                            continue;

                        string name = Encoding.UTF8.GetString(nameBuffer, 0, (int)length);
                        _uniformMetadata[name] = new UniformInfo(type, size);
                        if (name.EndsWith("[0]", StringComparison.Ordinal))
                        {
                            string baseName = name[..^3];
                            _uniformMetadata[baseName] = new UniformInfo(type, size);
                        }
                    }
                }
            }

            public void BeginBindingBatch()
            {
                Interlocked.Exchange(ref _uniformBindingAttempts, 0);
                Interlocked.Exchange(ref _uniformBindings, 0);
                Interlocked.Exchange(ref _samplerBindingAttempts, 0);
                Interlocked.Exchange(ref _samplerBindings, 0);
            }

            private bool MarkUniformBinding(int location)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.MarkUniformBinding");

                if (location < 0)
                    return false;

                Interlocked.Increment(ref _uniformBindingAttempts);
                Interlocked.Increment(ref _uniformBindings);
                
                return true;
            }

            private bool MarkSamplerBinding(int location)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.MarkSamplerBinding");

                if (location < 0)
                    return false;

                Interlocked.Increment(ref _samplerBindingAttempts);
                Interlocked.Increment(ref _samplerBindings);
                
                return true;
            }

            public void WarnIfNoUniformOrSamplerBindings(string? materialName)
            {
                int attempts = _uniformBindingAttempts + _samplerBindingAttempts;
                int successes = _uniformBindings + _samplerBindings;
                if (attempts == 0 || successes > 0)
                    return;

                string programName = Data.Name ?? BindingId.ToString();
                string matName = string.IsNullOrWhiteSpace(materialName) ? "<unnamed material>" : materialName!;
                string key = $"{programName}:{matName}";

                if (_loggedEmptyBindingBatches.TryAdd(key, 1))
                {
                    Debug.OpenGLError($"[Shader Binding] Program '{programName}' rendered material '{matName}' with no uniforms or samplers bound. " +
                        "Check that material parameter and sampler names match the shader.");
                }
            }

            private bool ValidateUniformType(int location, params GLEnum[] expectedTypes)
            {
                /*
                using var sample = Engine.Profiler.Start("GLRenderProgram.ValidateUniformType (by location)");

                if (location < 0)
                    return false;

                if (_locationNameCache.TryGetValue(location, out string? name))
                    return ValidateUniformType(name, location, expectedTypes);
                */
                return true;
            }

            private bool ValidateUniformType(string name, int? location, params GLEnum[] expectedTypes)
            {
                /*
                using var sample = Engine.Profiler.Start("GLRenderProgram.ValidateUniformType (by name)");

                if (!_uniformMetadata.TryGetValue(name, out var meta))
                    return true;

                foreach (var expected in expectedTypes)
                    if (meta.Type == expected)
                        return true;

                if (_loggedUniformMismatches.TryAdd(name, 0))
                {
                    string expectedDesc = string.Join(", ", expectedTypes);
                    string locDesc = location.HasValue ? location.Value.ToString() : "unknown";
                    Debug.LogWarning($"Uniform '{name}' (location {locDesc}) expects GL type {meta.Type} (size {meta.Size}) but received upload for {expectedDesc}. Skipping to avoid GL_INVALID_OPERATION.");
                }
                */
                //return false;
                return true;
            }

            public override void Destroy()
            {
                base.Destroy();
                Reset();
            }

            //TODO: serialize this cache and load on startup
            private static ConcurrentDictionary<ulong, BinaryProgram>? BinaryCache = null;

            internal static void ReadBinaryShaderCache(string currentVer)
            {
                if (BinaryCache is not null)
                    return;

                BinaryCache = new();

                string dir = Environment.CurrentDirectory;
                string path = Path.Combine(dir, "ShaderCache");
                if (!Directory.Exists(path))
                    return;

                foreach (string filePath in Directory.EnumerateFiles(path, "*.bin"))
                {
                    string name = Path.GetFileNameWithoutExtension(filePath);
                    string[] parts = name.Split('-');
                    if (parts.Length != 3)
                    {
                        Debug.OpenGLWarning($"Invalid binary shader cache file name, deleting: {name}");
                        File.Delete(filePath);
                        continue;
                    }

                    string fileVer = parts[2];
                    if (!string.Equals(currentVer, fileVer, StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.OpenGLWarning($"Binary shader cache file version mismatch, deleting: {fileVer} / {currentVer}");
                        File.Delete(filePath);
                        continue;
                    }

                    try
                    {
                        if (ulong.TryParse(parts[0], out ulong hash))
                        {
                            byte[] binary = File.ReadAllBytes(filePath);
                            GLEnum format = (GLEnum)Enum.Parse(typeof(GLEnum), parts[1]);
                            BinaryProgram binaryProgram = (binary, format, (uint)binary.Length);
                            BinaryCache.TryAdd(hash, binaryProgram);
                        }
                    }
                    catch
                    {

                    }
                }
            }
            private void WriteToBinaryShaderCache(BinaryProgram binary)
            {
                string ver;
                unsafe
                {
                    ver = new((sbyte*)Api.GetString(StringName.Version));
                }

                string dir = Environment.CurrentDirectory;
                string path = Path.Combine(dir, "ShaderCache");
                if (!Directory.Exists(path))
                    Directory.CreateDirectory(path);
                path = Path.Combine(path, $"{Hash}-{binary.Format}-{ver}.bin");
                File.WriteAllBytes(path, binary.Binary);
            }
            
            public static void DeleteFromBinaryShaderCache(ulong hash, GLEnum format)
            {
                BinaryCache?.TryRemove(hash, out _);
                //Delete any matching file (hash-format-*.bin pattern)
                string dir = Environment.CurrentDirectory;
                string path = Path.Combine(dir, "ShaderCache");
                if (!Directory.Exists(path))
                    return;
                    
                // Search for files matching the hash-format pattern with any version
                string pattern = $"{hash}-{format}-*.bin";
                foreach (string filePath in Directory.EnumerateFiles(path, pattern))
                {
                    try
                    {
                        File.Delete(filePath);
                    }
                    catch
                    {
                        // Ignore deletion failures (file may be in use)
                    }
                }
            }

            public ulong Hash { get; private set; }
            private BinaryProgram? _cachedProgram = null;

            public bool HasUniform(string name)
            {
                if (string.IsNullOrWhiteSpace(name))
                    return false;

                if (_uniformMetadata.ContainsKey(name))
                    return true;

                int loc = GetUniformLocation(name);
                return loc >= 0;
            }

            protected override uint CreateObject()
            {
                Reset();

                uint handle = Api.CreateProgram();

                Api.ProgramParameter(handle, GLEnum.ProgramSeparable, Data.Separable ? 1 : 0);

                return handle;
            }

            //private static object HashLock = new();
            private static readonly ConcurrentBag<ulong> Failed = [];
            public bool Link(bool force = false)
            {
                if (IsLinked)
                {
                    Api.GetProgram(BindingId, GLEnum.LinkStatus, out int s);
                    return s != 0;
                }

                if (!LinkReady && !force)
                    return false;

                //if (!IsGenerated)
                //{
                //    Generate();
                //    return false;
                //}

                //if (IsLinked)
                //    return true;

                if (_shaderCache.IsEmpty/* || _shaderCache.Values.Any(x => !x.IsCompiled)*/)
                    return false;

                bool isCached = false;
                uint bindingId = BindingId;
                BinaryProgram binProg = default;

                //lock (HashLock)
                //{
                    Hash = GetDeterministicHashCode(string.Join(' ', Data.Shaders.Select(x => x.Source.Text ?? string.Empty)));
                    if (Engine.Rendering.Settings.AllowBinaryProgramCaching)
                        isCached = BinaryCache?.TryGetValue(Hash, out binProg) ?? false;
                    
                    if (isCached)
                    {
                        //Debug.Out($"Using cached program binary with hash {Hash}.");
                        _cachedProgram = binProg;
                        GLEnum format = binProg.Format;
                        fixed (byte* ptr = binProg.Binary)
                            Api.ProgramBinary(bindingId, format, ptr, binProg.Length);
                        var error = Api.GetError();
                        if (error != GLEnum.NoError)
                        {
                            Debug.OpenGLWarning($"Failed to load cached program binary with format {format} and hash {Hash}: {error}. Deleting from cache.");
                            DeleteFromBinaryShaderCache(Hash, format);
                        }
                        else
                        {
                            IsLinked = true;
                            CacheActiveUniforms();
                            return true;
                        }
                    }

                    if (Failed.Contains(Hash))
                        return false;
                    else
                    {
                        _cachedProgram = null;

                        foreach (GLShader shader in _shaderCache.Values)
                            if (shader.Data.GenerateAsync)
                                Engine.EnqueueMainThreadTask(shader.Generate);
                            else
                                shader.Generate();

                        if (_shaderCache.Values.Any(x => !x.IsCompiled))
                        {
                            Debug.OpenGLWarning($"Failed to compile program with hash {Hash}.");
                            Failed.Add(Hash);
                            //TODO: return invalid material until shaders are compiled
                            return false;
                        }
                        
                        //Debug.Out($"Compiled program with hash {Hash}.");
                        var shaderCache = _shaderCache.Values;
                        GLShader?[] attached = new GLShader?[shaderCache.Count];
                        int i = 0;
                        bool noErrors = true;
                        foreach (GLShader shader in shaderCache)
                        {
                            if (shader.IsCompiled)
                            {
                                Api.AttachShader(bindingId, shader.BindingId);
                                attached[i++] = shader;
                            }
                            else
                            {
                                if (noErrors)
                                {
                                    noErrors = false;
                                    Debug.OpenGLWarning("One or more shaders failed to compile, can't link program.");
                                }

                                string? text = shader.Data.Source.Text;
                                if (text is not null)
                                    Debug.OpenGL(text);
                            }
                        }
                        if (noErrors)
                        {
                            Api.LinkProgram(bindingId);
                            Api.GetProgram(bindingId, GLEnum.LinkStatus, out int status);
                            bool linked = status != 0;
                            if (linked)
                                CacheBinary(bindingId);
                            else
                                PrintLinkDebug(bindingId);
                            IsLinked = linked;
                            if (IsLinked)
                                CacheActiveUniforms();
                        }
                        foreach (GLShader? shader in attached)
                        {
                            if (shader is null)
                                continue;

                            Api.DetachShader(BindingId, shader.BindingId);
                        }
                        _shaderCache.ForEach(x =>
                        {
                            x.Value.Destroy();
                        });
                        return IsLinked;
                    }
                //}
            }

            private void Value_SourceChanged()
            {
                //If the source of a shader changes, we need to relink the program.
                //This will cause the program to be destroyed and recreated.
                if (IsLinked)
                    Relink();
            }

            private void Relink()
            {
                if (Engine.InvokeOnMainThread(Relink, "GLRenderProgram.Relink"))
                    return;

                //Programs can't be relinked; destroy and recreate.
                Destroy();
                Generate();
                Link();
            }

            private void PrintLinkDebug(uint bindingId)
            {
                Api.GetProgramInfoLog(bindingId, out string info);
                Debug.OpenGL(string.IsNullOrWhiteSpace(info)
                    ? "Unable to link program, but no error was returned."
                    : info);

                //if (info.Contains("Vertex info"))
                //{
                //    RenderShader s = _shaders.FirstOrDefault(x => x.File.Type == EShaderMode.Vertex);
                //    string source = s.GetSource(true);
                //    Engine.PrintLine(source);
                //}
                //else if (info.Contains("Geometry info"))
                //{
                //    RenderShader s = _shaders.FirstOrDefault(x => x.File.Type == EShaderMode.Geometry);
                //    string source = s.GetSource(true);
                //    Engine.PrintLine(source);
                //}
                //else if (info.Contains("Fragment info"))
                //{
                //    RenderShader s = _shaders.FirstOrDefault(x => x.File.Type == EShaderMode.Fragment);
                //    string source = s.GetSource(true);
                //    Engine.PrintLine(source);
                //}
            }

            private void CacheBinary(uint bindingId)
            {
                if (!Engine.Rendering.Settings.AllowBinaryProgramCaching)
                    return;

                Api.GetProgram(bindingId, GLEnum.ProgramBinaryLength, out int len);
                if (len <= 0)
                    return;

                byte[] binary = new byte[len];
                GLEnum format;
                uint binaryLength;
                fixed (byte* ptr = binary)
                {
                    Api.GetProgramBinary(bindingId, (uint)len, &binaryLength, &format, ptr);
                }
                BinaryProgram bin = (binary, format, binaryLength);
                BinaryCache.TryAdd(Hash, bin);
                WriteToBinaryShaderCache(bin);
            }

            private static ulong CalcHash(IEnumerable<string> enumerable)
            {
                ulong hash = 17ul;
                foreach (string item in enumerable)
                    hash = hash * 31ul + GetDeterministicHashCode(item);
                return hash;
            }

            static ulong GetDeterministicHashCode(string str)
            {
                unchecked
                {
                    ulong hash1 = (5381 << 16) + 5381;
                    ulong hash2 = hash1;

                    for (int i = 0; i < str.Length; i += 2)
                    {
                        hash1 = ((hash1 << 5) + hash1) ^ str[i];
                        if (i == str.Length - 1)
                            break;
                        hash2 = ((hash2 << 5) + hash2) ^ str[i + 1];
                    }

                    ulong value = hash1 + (hash2 * 1566083941ul);
                    //Debug.Out(value.ToString());
                    return value;
                }
            }

            public void Use()
                => Api.UseProgram(BindingId);

            public IEnumerator<GLShader> GetEnumerator()
                => _shaderCache.Values.GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator()
                => _shaderCache.Values.GetEnumerator();

            #region Uniforms
            public void Uniform(EEngineUniform name, Vector2 p)
                => Uniform(name.ToStringFast(), p);
            public void Uniform(EEngineUniform name, Vector3 p)
                => Uniform(name.ToStringFast(), p);
            public void Uniform(EEngineUniform name, Vector4 p)
                => Uniform(name.ToStringFast(), p);
            public void Uniform(EEngineUniform name, Quaternion p)
                => Uniform(name.ToStringFast(), p);
            public void Uniform(EEngineUniform name, int p)
                => Uniform(name.ToStringFast(), p);
            public void Uniform(EEngineUniform name, float p)
                => Uniform(name.ToStringFast(), p);
            public void Uniform(EEngineUniform name, uint p)
                => Uniform(name.ToStringFast(), p);
            public void Uniform(EEngineUniform name, double p)
                => Uniform(name.ToStringFast(), p);
            public void Uniform(EEngineUniform name, Matrix4x4 p)
                => Uniform(name.ToStringFast(), p);
            public void Uniform(EEngineUniform name, bool p)
                => Uniform(name.ToStringFast(), p);

            public void Uniform(string name, Vector2 p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, Vector3 p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, Vector4 p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, Quaternion p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, int p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, float p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, uint p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, double p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, Matrix4x4 p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, bool p)
                => Uniform(GetUniformLocation(name), p);

            public void Uniform(int location, Vector2 p)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.Uniform(Vector2)");

                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.FloatVec2))
                    return;

                Api.ProgramUniform2(BindingId, location, p);
            }
            public void Uniform(int location, Vector3 p)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.Uniform(Vector3)");

                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.FloatVec3))
                    return;

                Api.ProgramUniform3(BindingId, location, p);
            }
            public void Uniform(int location, Vector4 p)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.Uniform(Vector4)");

                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.FloatVec4))
                    return;

                Api.ProgramUniform4(BindingId, location, p);
            }
            public void Uniform(int location, Quaternion p)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.Uniform(Quaternion)");

                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.FloatVec4))
                    return;

                Api.ProgramUniform4(BindingId, location, p);
            }
            public void Uniform(int location, int p)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.Uniform(Int)");

                if (!MarkUniformBinding(location)) 
                    return;

                // int uniforms are also used for samplers, so accept Int, Bool, and all sampler types
                if (!ValidateUniformType(location, GLEnum.Int, GLEnum.Bool, GLEnum.Sampler2D, GLEnum.Sampler3D, GLEnum.SamplerCube, 
                    GLEnum.Sampler2DShadow, GLEnum.Sampler2DArray, GLEnum.SamplerCubeShadow, GLEnum.IntSampler2D, GLEnum.IntSampler3D,
                    GLEnum.UnsignedIntSampler2D, GLEnum.UnsignedIntSampler3D, GLEnum.Sampler2DRect, GLEnum.Sampler2DRectShadow,
                    GLEnum.Sampler1D, GLEnum.Sampler1DShadow, GLEnum.Sampler1DArray, GLEnum.Sampler1DArrayShadow, GLEnum.Sampler2DArrayShadow,
                    GLEnum.SamplerBuffer, GLEnum.Sampler2DMultisample, GLEnum.Sampler2DMultisampleArray, GLEnum.IntSampler2DArray,
                    GLEnum.Image2D, GLEnum.Image3D, GLEnum.ImageCube, GLEnum.Image2DArray))
                    return;

                Api.ProgramUniform1(BindingId, location, p);
            }
            public void Uniform(int location, float p)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.Uniform(Float)");

                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.Float))
                    return;

                Api.ProgramUniform1(BindingId, location, p);
            }
            public void Uniform(int location, uint p)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.Uniform(UInt)");

                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.UnsignedInt))
                    return;

                Api.ProgramUniform1(BindingId, location, p);
            }
            public void Uniform(int location, double p)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.Uniform(Double)");

                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.Double))
                    return;

                Api.ProgramUniform1(BindingId, location, p);
            }
            public void Uniform(int location, Matrix4x4 p)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.Uniform(Matrix4x4)");

                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.FloatMat4))
                    return;

                Api.ProgramUniformMatrix4(BindingId, location, 1, false, &p.M11);
            }
            public void Uniform(int location, bool p)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.Uniform(Bool)");

                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.Bool, GLEnum.Int))
                    return;

                Api.ProgramUniform1(BindingId, location, p ? 1 : 0);
            }

            public void Uniform(string name, Vector2[] p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, Vector3[] p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, Vector4[] p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, Quaternion[] p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, int[] p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, float[] p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, Span<float> p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, uint[] p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, double[] p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, Matrix4x4[] p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, bool[] p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, BoolVector2 p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, BoolVector3 p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, BoolVector4 p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, BoolVector2[] p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, BoolVector3[] p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, BoolVector4[] p)
                => Uniform(GetUniformLocation(name), p);

            public void Uniform(int location, IVector2 p)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.Uniform(IVector2)");

                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.IntVec2))
                    return;

                Api.ProgramUniform2(BindingId, location, p.X, p.Y);
            }
            public void Uniform(int location, IVector3 p)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.Uniform(IVector3)");

                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.IntVec3))
                    return;

                Api.ProgramUniform3(BindingId, location, p.X, p.Y, p.Z);
            }
            public void Uniform(int location, IVector4 p)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.Uniform(IVector4)");

                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.IntVec4))
                    return;

                Api.ProgramUniform4(BindingId, location, p.X, p.Y, p.Z, p.W);
            }
            public void Uniform(int location, IVector2[] p)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.Uniform(IVector2[])");

                if (!MarkUniformBinding(location) || p.Length == 0)
                    return;

                if (!ValidateUniformType(location, GLEnum.IntVec2))
                    return;

                fixed (IVector2* ptr = p)
                {
                    Api.ProgramUniform2(BindingId, location, (uint)p.Length, (int*)ptr);
                }
            }
            public void Uniform(int location, IVector3[] p)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.Uniform(IVector3[])");

                if (!MarkUniformBinding(location) || p.Length == 0)
                    return;

                if (!ValidateUniformType(location, GLEnum.IntVec3))
                    return;

                fixed (IVector3* ptr = p)
                {
                    Api.ProgramUniform3(BindingId, location, (uint)p.Length, (int*)ptr);
                }
            }
            public void Uniform(int location, IVector4[] p)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.Uniform(IVector4[])");

                if (!MarkUniformBinding(location) || p.Length == 0)
                    return;

                if (!ValidateUniformType(location, GLEnum.IntVec4))
                    return;

                fixed (IVector4* ptr = p)
                {
                    Api.ProgramUniform4(BindingId, location, (uint)p.Length, (int*)ptr);
                }
            }

            public void Uniform(string name, IVector2 p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, IVector3 p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, IVector4 p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, IVector2[] p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, IVector3[] p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, IVector4[] p)
                => Uniform(GetUniformLocation(name), p);

            public void Uniform(int location, Vector2[] p)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.Uniform(Vector2[])");

                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.FloatVec2))
                    return;

                fixed (Vector2* ptr = p)
                {
                    Api.ProgramUniform2(BindingId, location, (uint)p.Length, (float*)ptr);
                }
            }
            public void Uniform(int location, Vector3[] p)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.Uniform(Vector3[])");

                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.FloatVec3))
                    return;

                fixed (Vector3* ptr = p)
                {
                    Api.ProgramUniform3(BindingId, location, (uint)p.Length, (float*)ptr);
                }
            }
            public void Uniform(int location, Vector4[] p)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.Uniform(Vector4[])");

                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.FloatVec4))
                    return;

                fixed (Vector4* ptr = p)
                {
                    Api.ProgramUniform4(BindingId, location, (uint)p.Length, (float*)ptr);
                }
            }
            public void Uniform(int location, Quaternion[] p)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.Uniform(Quaternion[])");

                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.FloatVec4))
                    return;

                fixed (Quaternion* ptr = p)
                {
                    Api.ProgramUniform4(BindingId, location, (uint)p.Length, (float*)ptr);
                }
            }
            public void Uniform(int location, int[] p)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.Uniform(Int[])");

                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.Int, GLEnum.Bool))
                    return;

                fixed (int* ptr = p)
                {
                    Api.ProgramUniform1(BindingId, location, (uint)p.Length, ptr);
                }
            }
            public void Uniform(int location, float[] p)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.Uniform(Float[])");

                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.Float))
                    return;

                fixed (float* ptr = p)
                {
                    Api.ProgramUniform1(BindingId, location, (uint)p.Length, ptr);
                }
            }
            public void Uniform(int location, Span<float> p)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.Uniform(Span<Float>)");

                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.Float))
                    return;

                unsafe
                {
                    fixed (float* ptr = p)
                    {
                        Api.ProgramUniform1(BindingId, location, (uint)p.Length, ptr);
                    }
                }
            }
            public void Uniform(int location, uint[] p)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.Uniform(UInt[])");

                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.UnsignedInt))
                    return;

                fixed (uint* ptr = p)
                {
                    Api.ProgramUniform1(BindingId, location, (uint)p.Length, ptr);
                }
            }
            public void Uniform(int location, double[] p)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.Uniform(Double[])");

                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.Double))
                    return;

                fixed (double* ptr = p)
                {
                    Api.ProgramUniform1(BindingId, location, (uint)p.Length, ptr);
                }
            }
            public void Uniform(int location, Matrix4x4[] p)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.Uniform(Matrix4x4[])");

                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.FloatMat4))
                    return;

                fixed (Matrix4x4* ptr = p)
                {
                    Api.ProgramUniformMatrix4(BindingId, location, (uint)p.Length, false, (float*)ptr);
                }
            }
            public void Uniform(int location, bool[] p)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.Uniform(Bool[])");

                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.Bool, GLEnum.Int))
                    return;

                int[] conv = new int[p.Length];
                for (int i = 0; i < p.Length; i++)
                    conv[i] = p[i] ? 1 : 0;

                fixed (int* ptr = conv)
                {
                    Api.ProgramUniform1(BindingId, location, (uint)conv.Length, ptr);
                }
            }

            public void Uniform(int location, BoolVector2 p)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.Uniform(BoolVector2)");

                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.BoolVec2, GLEnum.IntVec2))
                    return;

                Api.ProgramUniform2(BindingId, location, p.X ? 1 : 0, p.Y ? 1 : 0);
            }
            public void Uniform(int location, BoolVector3 p)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.Uniform(BoolVector3)");

                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.BoolVec3, GLEnum.IntVec3))
                    return;

                Api.ProgramUniform3(BindingId, location, p.X ? 1 : 0, p.Y ? 1 : 0, p.Z ? 1 : 0);
            }
            public void Uniform(int location, BoolVector4 p)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.Uniform(BoolVector4)");

                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.BoolVec4, GLEnum.IntVec4))
                    return;

                Api.ProgramUniform4(BindingId, location, p.X ? 1 : 0, p.Y ? 1 : 0, p.Z ? 1 : 0, p.W ? 1 : 0);
            }

            public void Uniform(int location, BoolVector2[] p)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.Uniform(BoolVector2[])");

                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.BoolVec2, GLEnum.IntVec2))
                    return;

                int[] conv = new int[p.Length * 2];
                for (int i = 0; i < p.Length; i++)
                {
                    conv[i * 2] = p[i].X ? 1 : 0;
                    conv[i * 2 + 1] = p[i].Y ? 1 : 0;
                }
                fixed (int* ptr = conv)
                {
                    Api.ProgramUniform2(BindingId, location, (uint)p.Length, ptr);
                }
            }
            public void Uniform(int location, BoolVector3[] p)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.Uniform(BoolVector3[])");

                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.BoolVec3, GLEnum.IntVec3))
                    return;

                int[] conv = new int[p.Length * 3];
                for (int i = 0; i < p.Length; i++)
                {
                    conv[i * 3] = p[i].X ? 1 : 0;
                    conv[i * 3 + 1] = p[i].Y ? 1 : 0;
                    conv[i * 3 + 2] = p[i].Z ? 1 : 0;
                }

                fixed (int* ptr = conv)
                {
                    Api.ProgramUniform3(BindingId, location, (uint)p.Length, ptr);
                }
            }
            public void Uniform(int location, BoolVector4[] p)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.Uniform(BoolVector4[])");

                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.BoolVec4, GLEnum.IntVec4))
                    return;

                int[] conv = new int[p.Length * 4];
                for (int i = 0; i < p.Length; i++)
                {
                    conv[i * 4] = p[i].X ? 1 : 0;
                    conv[i * 4 + 1] = p[i].Y ? 1 : 0;
                    conv[i * 4 + 2] = p[i].Z ? 1 : 0;
                    conv[i * 4 + 3] = p[i].W ? 1 : 0;
                }
                fixed (int* ptr = conv)
                {
                    Api.ProgramUniform4(BindingId, location, (uint)p.Length, ptr);
                }
            }
            #endregion

            #region Samplers
            public void Sampler(int location, XRTexture texture, int textureUnit)
            {
                var glObj = Renderer.GetOrCreateAPIRenderObject(texture);
                if (glObj is not IGLTexture glTex)
                    return;

                Sampler(location, glTex, textureUnit);
            }

            public void Sampler(string name, XRTexture texture, int textureUnit)
            {
                int location = GetUniformLocation(name);

                // If the uniform is optimized out (e.g., layout(binding=) samplers), still bind the texture to the unit
                // so fixed-binding samplers can sample correctly. Log once when the uniform is missing.
                if (location < 0 && Engine.Rendering.Settings.LogMissingShaderSamplers)
                {
                    string key = $"{Data.Name ?? BindingId.ToString()}:{name}:{textureUnit}";
                    if (_loggedUniformMismatches.TryAdd(key, 1))
                    {
                        Debug.OpenGLWarning($"[Shader Texture Binding] Sampler '{name}' not found in program '{Data.Name ?? BindingId.ToString()}' for texture unit {textureUnit}. " +
                            $"Texture: '{texture.Name}', SamplerName: '{texture.SamplerName}'. " +
                            $"Binding anyway due to fixed sampler binding.");
                    }
                }

                // Always bind to the requested unit; Uniform() will be a no-op if location < 0.
                Sampler(location, texture, textureUnit);
            }

            /// <summary>
            /// Passes a texture sampler into the fragment shader of this program by name.
            /// The name is cached so that retrieving the sampler's location is only required once.
            /// </summary>
            public void Sampler(string name, IGLTexture texture, int textureUnit)
            {
                int location = GetUniformLocation(name);

                if (location < 0 && Engine.Rendering.Settings.LogMissingShaderSamplers)
                {
                    string key = $"{Data.Name ?? BindingId.ToString()}:{name}:{textureUnit}";
                    if (_loggedUniformMismatches.TryAdd(key, 1))
                    {
                        string texName = texture.Data?.Name ?? "unknown";
                        string samplerName = (texture.Data as XRTexture)?.SamplerName ?? "null";
                        Debug.OpenGLWarning($"[Shader Texture Binding] Sampler '{name}' not found in program '{Data.Name ?? BindingId.ToString()}' for texture unit {textureUnit}. " +
                            $"Texture: '{texName}', SamplerName: '{samplerName}'. " +
                            $"Binding anyway due to fixed sampler binding.");
                    }
                }

                Sampler(location, texture, textureUnit);
            }

            /// <summary>
            /// Passes a texture sampler value into the fragment shader of this program by location.
            /// </summary>
            public void Sampler(int location, IGLTexture texture, int textureUnit)
            {
                // Even if the uniform location is invalid (-1), we still want the GL state
                // to have the texture bound at the requested unit for layout(binding=) samplers.
                bool canBindUniform = MarkSamplerBinding(location);

                texture.PreSampling();
                Renderer.SetActiveTextureUnit(textureUnit);

                if (canBindUniform && location >= 0)
                    Uniform(location, textureUnit);

                texture.Bind();
                texture.PostSampling();
            }
            #endregion
        }
    }

    internal record struct BinaryProgram(byte[] Binary, GLEnum Format, uint Length)
    {
        public static implicit operator (byte[] bin, GLEnum fmt, uint len)(BinaryProgram value)
            => (value.Binary, value.Format, value.Length);

        public static implicit operator BinaryProgram((byte[] bin, GLEnum fmt, uint len) value)
            => new(value.bin, value.fmt, value.len);
    }
}