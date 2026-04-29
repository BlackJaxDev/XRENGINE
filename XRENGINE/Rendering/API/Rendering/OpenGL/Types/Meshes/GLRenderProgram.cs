using XREngine.Extensions;
using Silk.NET.OpenGL;
using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Linq;
using System.Text;
using System.Threading;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders;
using static XREngine.Rendering.XRRenderProgram;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        public delegate void DelCompile(bool compiledSuccessfully, string? compileInfo);
        public partial class GLRenderProgram(OpenGLRenderer renderer, XRRenderProgram data) : GLObject<XRRenderProgram>(renderer, data), IEnumerable<GLShader>
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
            private readonly ConcurrentDictionary<string, int> _explicitAttributeLocations = new(StringComparer.Ordinal);
            private readonly object _explicitAttributeLocationLock = new();

            private readonly ConcurrentDictionary<int, string> _locationNameCache = new();
            private readonly ConcurrentDictionary<string, UniformInfo> _uniformMetadata = new();
            private SamplerUniformInfo[] _activeSamplerUniforms = [];
            private readonly ConcurrentDictionary<string, byte> _loggedUniformMismatches = new();

            // Binding-batch sampler tracking is scoped to the active draw on the render thread.
            private readonly HashSet<int> _boundSamplerLocations = [];
            private readonly HashSet<int> _boundSamplerUnits = [];
            private readonly HashSet<string> _boundSamplerNames = new(StringComparer.Ordinal);
            private readonly HashSet<string> _suppressedFallbackSamplerNames = new(StringComparer.Ordinal);

            private int _uniformBindingAttempts;
            private int _uniformBindings;
            private int _samplerBindingAttempts;
            private int _samplerBindings;
            private readonly ConcurrentDictionary<string, byte> _loggedEmptyBindingBatches = new();
            private ulong _engineUniformFrameId = ulong.MaxValue;
            private EUniformRequirements _engineUniformRequirements = EUniformRequirements.None;
            private XRRenderPipelineInstance? _engineUniformPipeline;
            private XRCamera? _engineUniformCamera;
            private XRCamera? _engineUniformStereoRightEyeCamera;
            private XRWorldInstance? _engineUniformWorld;
            private bool _engineUniformStereoPass;
            private bool _engineUniformUseUnjitteredProjection;
            private int _engineUniformRenderAreaX;
            private int _engineUniformRenderAreaY;
            private int _engineUniformRenderAreaWidth;
            private int _engineUniformRenderAreaHeight;

            private readonly ConcurrentBag<string> _failedAttributes = [];
            private readonly ConcurrentDictionary<string, byte> _failedUniforms = new();
            private bool _explicitAttributeLocationsResolved;

            private readonly record struct UniformInfo(GLEnum Type, int Size);
            private readonly record struct SamplerUniformInfo(string Name, GLEnum Type);
            internal readonly record struct UniformMetadataEntry(string Name, GLEnum Type, int Size);

            private static XRTexture2D? _fallbackTexture2D;
            private static XRTexture2DArray? _fallbackTexture2DArray;
            private static XRTextureCube? _fallbackTextureCube;

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

            internal IReadOnlyCollection<int> GetBoundSamplerUnitsView()
                => _boundSamplerUnits;

            private void CacheUniformLocation(string name)
            {
                if (string.IsNullOrEmpty(name) || _uniformCache.ContainsKey(name))
                    return;

                int location = Api.GetUniformLocation(BindingId, name);
                _uniformCache.TryAdd(name, location);

                if (location >= 0)
                    _locationNameCache[location] = name;
                else
                    _failedUniforms.TryAdd(name, 0);
            }

            /// <summary>
            /// If the program has been generated and linked successfully,
            /// this will return the location of the attribute with the given name.
            /// Cached for performance and thread-safe.
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
                if (location < 0 && TryGetExplicitAttributeLocation(name, out int explicitLocation))
                    location = explicitLocation;

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

            private bool TryGetExplicitAttributeLocation(string name, out int location)
            {
                EnsureExplicitAttributeLocations();
                return _explicitAttributeLocations.TryGetValue(name, out location);
            }

            private void EnsureExplicitAttributeLocations()
            {
                if (_explicitAttributeLocationsResolved)
                    return;

                lock (_explicitAttributeLocationLock)
                {
                    if (_explicitAttributeLocationsResolved)
                        return;

                    foreach (var pair in GLShaderAttributeLayoutResolver.ResolveVertexInputLocations(Data.Shaders))
                        _explicitAttributeLocations.TryAdd(pair.Key, pair.Value);

                    _explicitAttributeLocationsResolved = true;
                }
            }

            public bool LinkReady => Data.LinkReady;

            private void Reset()
            {
                IsLinked = false;
                _asyncLinkPhase = EAsyncLinkPhase.Idle;
                _asyncAttachedShaderIds = null;
                _asyncLinkedProgramId = 0;
                _asyncBinaryUploadPending = false;
                _asyncCompileLinkPending = false;
                UnregisterPendingAsyncProgram();
                _hashComputed = false;
                InvalidatePreparedLinkData();
                _attribCache.Clear();
                _explicitAttributeLocations.Clear();
                _uniformCache.Clear();
                _failedAttributes.Clear();
                _failedUniforms.Clear();
                _locationNameCache.Clear();
                _uniformMetadata.Clear();
                _activeSamplerUniforms = [];
                _loggedUniformMismatches.Clear();
                _loggedEmptyBindingBatches.Clear();
                _boundSamplerLocations.Clear();
                _boundSamplerUnits.Clear();
                _boundSamplerNames.Clear();
                _suppressedFallbackSamplerNames.Clear();
                _uniformBindingAttempts = 0;
                _uniformBindings = 0;
                _samplerBindingAttempts = 0;
                _samplerBindings = 0;
                ResetEngineUniformBindingState();
                _explicitAttributeLocationsResolved = false;
            }

            public override void Destroy()
            {
                if (!Engine.IsRenderThread)
                {
                    Engine.EnqueueMainThreadTask(Destroy);
                    return;
                }

                ReleaseAsyncLinkState();
                base.Destroy();
                Reset();
            }

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

                    Api.ProgramParameter(handle, GLEnum.ProgramBinaryRetrievableHint, 1);
                Api.ProgramParameter(handle, GLEnum.ProgramSeparable, Data.Separable ? 1 : 0);

                return handle;
            }

            public void Use()
                => Api.UseProgram(BindingId);

            public IEnumerator<GLShader> GetEnumerator()
                => _shaderCache.Values.GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator()
                => _shaderCache.Values.GetEnumerator();
        }
    }
}
