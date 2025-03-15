using Extensions;
using Silk.NET.OpenGL;
using System.Collections;
using System.Collections.Concurrent;
using System.Numerics;
using XREngine.Data.Vectors;
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

            public override GLObjectType Type => GLObjectType.Program;

            private readonly ConcurrentDictionary<string, int>
                _uniformCache = new(),
                _attribCache = new();

            private readonly ConcurrentBag<string> _failedAttributes = [];
            private readonly ConcurrentBag<string> _failedUniforms = [];

            protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
            {
                base.OnPropertyChanged(propName, prev, field);
                switch (propName)
                {
                    case nameof(Data.Shaders):
                        if (IsLinked)
                        {
                            //Force a recompilation? Programs cannot be relinked.
                            Destroy();
                            Generate();
                            Link();
                        }
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
                if (!IsLinked)
                    return -1;

                if (_uniformCache.TryGetValue(name, out int value))
                    return value;

                if (!GetUniform(name, out value))
                    return -1;

                _uniformCache.TryAdd(name, value);
                return value;
            }
            private bool GetUniform(string name, out int location)
            {
                bool failed = _failedUniforms.Contains(name);
                if (failed)
                {
                    location = -1;
                    return false;
                }
                location = Api.GetUniformLocation(BindingId, name);
                if (location < 0)
                {
                    if (!failed)
                    {
                        _failedUniforms.Add(name);
                        //Debug.LogWarning($"Uniform {name} not found in OpenGL program.");
                    }
                    return false;
                }
                return true;
            }
            /// <summary>
            /// If the program has been generated and linked successfully,
            /// this will return the location of the attribute with the given name.
            /// Cached for performance and thread-safe.
            /// </summary>
            /// <param name="name"></param>
            /// <returns></returns>
            public int GetAttributeLocation(string name)
            {
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
                shader.ActivePrograms.Remove(this);
            }

            private void ShaderAdded(XRShader item)
            {
                _shaderCache.TryAdd(item, GetAndGenerate(item));
            }
            private GLShader GetAndGenerate(XRShader data)
            {
                GLShader shader = Renderer.GenericToAPI<GLShader>(data)!;
                //Engine.EnqueueMainThreadTask(shader.Generate);
                shader.ActivePrograms.Add(this);
                return shader;
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
                Data.UniformSetUIntArrayRequested += Uniform;
                Data.UniformSetDoubleArrayRequested += Uniform;
                Data.UniformSetMatrix4x4ArrayRequested += Uniform;

                Data.UniformSetIVector2Requested += Uniform;
                Data.UniformSetIVector3Requested += Uniform;
                Data.UniformSetIVector4Requested += Uniform;

                //Data.UniformSetUVector2Requested += Uniform;
                //Data.UniformSetUVector3Requested += Uniform;
                //Data.UniformSetUVector4Requested += Uniform;

                //Data.UniformSetBoolVector2Requested += Uniform;
                //Data.UniformSetBoolVector3Requested += Uniform;
                //Data.UniformSetBoolVector4Requested += Uniform;

                Data.SamplerRequested += Sampler;
                Data.SamplerRequestedByLocation += Sampler;
                Data.BindImageTextureRequested += BindImageTexture;
                Data.DispatchComputeRequested += DispatchCompute;

                foreach (XRShader shader in Data.Shaders)
                    ShaderAdded(shader);
                Data.Shaders.PostAnythingAdded += ShaderAdded;
                Data.Shaders.PostAnythingRemoved += ShaderRemoved;
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
                Data.UniformSetUIntArrayRequested -= Uniform;
                Data.UniformSetDoubleArrayRequested -= Uniform;
                Data.UniformSetMatrix4x4ArrayRequested -= Uniform;

                Data.UniformSetIVector2Requested -= Uniform;
                Data.UniformSetIVector3Requested -= Uniform;
                Data.UniformSetIVector4Requested -= Uniform;

                //Data.UniformSetUVector2Requested -= Uniform;
                //Data.UniformSetUVector3Requested -= Uniform;
                //Data.UniformSetUVector4Requested -= Uniform;

                //Data.UniformSetBoolVector2Requested -= Uniform;
                //Data.UniformSetBoolVector3Requested -= Uniform;
                //Data.UniformSetBoolVector4Requested -= Uniform;

                Data.SamplerRequested -= Sampler;
                Data.SamplerRequestedByLocation -= Sampler;
                Data.BindImageTextureRequested -= BindImageTexture;
                Data.DispatchComputeRequested -= DispatchCompute;

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
                        Debug.LogWarning($"Invalid binary shader cache file name, deleting: {name}");
                        File.Delete(filePath);
                        continue;
                    }

                    string fileVer = parts[2];
                    if (!string.Equals(currentVer, fileVer, StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.LogWarning($"Binary shader cache file version mismatch, deleting: {fileVer} / {currentVer}");
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
                //Delete the file
                string dir = Environment.CurrentDirectory;
                string path = Path.Combine(dir, "ShaderCache");
                string fileName = $"{hash}-{format}.bin";
                path = Path.Combine(path, fileName);
                if (File.Exists(path))
                    File.Delete(path);
            }

            public ulong Hash { get; private set; }
            private BinaryProgram? _cachedProgram = null;

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
                            Debug.LogWarning($"Failed to load cached program binary with format {format} and hash {Hash}: {error}. Deleting from cache.");
                            DeleteFromBinaryShaderCache(Hash, format);
                        }
                        else
                        {
                            IsLinked = true;
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
                            Debug.LogWarning($"Failed to compile program with hash {Hash}.");
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
                                    Debug.LogWarning("One or more shaders failed to compile, can't link program.");
                                }

                                string? text = shader.Data.Source.Text;
                                if (text is not null)
                                    Debug.Out(text);
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
                        }
                        foreach (GLShader? shader in attached)
                        {
                            if (shader is null)
                                continue;

                            Api.DetachShader(BindingId, shader.BindingId);
                        }
                        _shaderCache.ForEach(x => x.Value.Destroy());
                        return IsLinked;
                    }
                //}
            }

            private void PrintLinkDebug(uint bindingId)
            {
                Api.GetProgramInfoLog(bindingId, out string info);
                Debug.Out(string.IsNullOrWhiteSpace(info)
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
                => ((IEnumerable<GLShader>)_shaderCache).GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator()
                => ((IEnumerable<GLShader>)_shaderCache).GetEnumerator();

            #region Uniforms
            public void Uniform(EEngineUniform name, Vector2 p)
                => Uniform(name.ToString(), p);
            public void Uniform(EEngineUniform name, Vector3 p)
                => Uniform(name.ToString(), p);
            public void Uniform(EEngineUniform name, Vector4 p)
                => Uniform(name.ToString(), p);
            public void Uniform(EEngineUniform name, Quaternion p)
                => Uniform(name.ToString(), p);
            public void Uniform(EEngineUniform name, int p)
                => Uniform(name.ToString(), p);
            public void Uniform(EEngineUniform name, float p)
                => Uniform(name.ToString(), p);
            public void Uniform(EEngineUniform name, uint p)
                => Uniform(name.ToString(), p);
            public void Uniform(EEngineUniform name, double p)
                => Uniform(name.ToString(), p);
            public void Uniform(EEngineUniform name, Matrix4x4 p)
                => Uniform(name.ToString(), p);
            public void Uniform(EEngineUniform name, bool p)
                => Uniform(name.ToString(), p);

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
                => Api.ProgramUniform2(BindingId, location, p);
            public void Uniform(int location, Vector3 p)
                => Api.ProgramUniform3(BindingId, location, p);
            public void Uniform(int location, Vector4 p)
                => Api.ProgramUniform4(BindingId, location, p);
            public void Uniform(int location, Quaternion p)
                => Api.ProgramUniform4(BindingId, location, p);
            public void Uniform(int location, int p)
                => Api.ProgramUniform1(BindingId, location, p);
            public void Uniform(int location, float p)
                => Api.ProgramUniform1(BindingId, location, p);
            public void Uniform(int location, uint p)
                => Api.ProgramUniform1(BindingId, location, p);
            public void Uniform(int location, double p)
                => Api.ProgramUniform1(BindingId, location, p);
            public void Uniform(int location, Matrix4x4 p)
                => Api.ProgramUniformMatrix4(BindingId, location, 1, false, &p.M11);
            public void Uniform(int location, bool p)
                => Api.ProgramUniform1(BindingId, location, p ? 1 : 0);

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
            public void Uniform(string name, uint[] p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, double[] p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, Matrix4x4[] p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, bool[] p)
                => Uniform(GetUniformLocation(name), p);

            public void Uniform(int location, IVector2 p)
                => Api.ProgramUniform2(BindingId, location, p);
            public void Uniform(int location, IVector3 p)
                => Api.ProgramUniform3(BindingId, location, p);
            public void Uniform(int location, IVector4 p)
                => Api.ProgramUniform4(BindingId, location, p);

            public void Uniform(string name, IVector2 p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, IVector3 p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, IVector4 p)
                => Uniform(GetUniformLocation(name), p);

            public void Uniform(int location, Vector2[] p)
            {
                if (location < 0)
                    return;
                fixed (Vector2* ptr = p)
                {
                    Api.ProgramUniform2(BindingId, location, (uint)p.Length, (float*)ptr);
                }
            }
            public void Uniform(int location, Vector3[] p)
            {
                if (location < 0)
                    return;
                fixed (Vector3* ptr = p)
                {
                    Api.ProgramUniform3(BindingId, location, (uint)p.Length, (float*)ptr);
                }
            }
            public void Uniform(int location, Vector4[] p)
            {
                if (location < 0)
                    return;
                fixed (Vector4* ptr = p)
                {
                    Api.ProgramUniform4(BindingId, location, (uint)p.Length, (float*)ptr);
                }
            }
            public void Uniform(int location, Quaternion[] p)
            {
                if (location < 0)
                    return;
                fixed (Quaternion* ptr = p)
                {
                    Api.ProgramUniform4(BindingId, location, (uint)p.Length, (float*)ptr);
                }
            }
            public void Uniform(int location, int[] p)
            {
                if (location < 0)
                    return;
                fixed (int* ptr = p)
                {
                    Api.ProgramUniform1(BindingId, location, (uint)p.Length, ptr);
                }
            }
            public void Uniform(int location, float[] p)
            {
                if (location < 0)
                    return;
                fixed (float* ptr = p)
                {
                    Api.ProgramUniform1(BindingId, location, (uint)p.Length, ptr);
                }
            }
            public void Uniform(int location, uint[] p)
            {
                if (location < 0)
                    return;
                fixed (uint* ptr = p)
                {
                    Api.ProgramUniform1(BindingId, location, (uint)p.Length, ptr);
                }
            }
            public void Uniform(int location, double[] p)
            {
                if (location < 0)
                    return;
                fixed (double* ptr = p)
                {
                    Api.ProgramUniform1(BindingId, location, (uint)p.Length, ptr);
                }
            }
            public void Uniform(int location, Matrix4x4[] p)
            {
                if (location < 0)
                    return;
                fixed (Matrix4x4* ptr = p)
                {
                    Api.ProgramUniformMatrix4(BindingId, location, (uint)p.Length, false, (float*)ptr);
                }
            }
            public void Uniform(int location, bool[] p)
            {
                if (location < 0)
                    return;

                int[] conv = new int[p.Length];
                for (int i = 0; i < p.Length; i++)
                    conv[i] = p[i] ? 1 : 0;
                Uniform(location, conv);
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
                if (!GetUniform(name, out int location))
                    return;

                Sampler(location, texture, textureUnit);
            }
            /// <summary>
            /// Passes a texture sampler into the fragment shader of this program by name.
            /// The name is cached so that retrieving the sampler's location is only required once.
            /// </summary>
            public void Sampler(string name, IGLTexture texture, int textureUnit)
            {
                if (!GetUniform(name, out int location))
                    return;
                
                Sampler(location, texture, textureUnit);
            }

            /// <summary>
            /// Passes a texture sampler value into the fragment shader of this program by location.
            /// </summary>
            public void Sampler(int location, IGLTexture texture, int textureUnit)
            {
                if (location < 0)
                    return;

                texture.PreSampling();
                Renderer.SetActiveTexture(textureUnit);
                Uniform(location, textureUnit);
                texture.Bind();
                texture.PostSampling();
            }
            #endregion
        }

        private void SetActiveTexture(int textureUnit)
            => Api.ActiveTexture(GLEnum.Texture0 + textureUnit);
    }

    internal record struct BinaryProgram(byte[] Binary, GLEnum Format, uint Length)
    {
        public static implicit operator (byte[] bin, GLEnum fmt, uint len)(BinaryProgram value)
            => (value.Binary, value.Format, value.Length);

        public static implicit operator BinaryProgram((byte[] bin, GLEnum fmt, uint len) value)
            => new(value.bin, value.fmt, value.len);
    }
}