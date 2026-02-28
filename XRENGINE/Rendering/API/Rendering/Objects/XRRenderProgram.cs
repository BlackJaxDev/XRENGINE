using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering.Models.Materials;
using YamlDotNet.Serialization;

namespace XREngine.Rendering
{
    public class XRRenderProgram : GenericRenderObject, IEnumerable<XRShader>
    {
        public sealed record ShaderUniformBinding(
            string Name,
            string GlslType,
            EShaderVarType? EngineType,
            bool IsArray,
            int? ArrayLength,
            string? ArrayLengthExpression,
            IReadOnlyList<EShaderType> DeclaredIn);

        public sealed record ShaderTextureBinding(
            string Name,
            string GlslType,
            bool IsArray,
            int? ArrayLength,
            string? ArrayLengthExpression,
            IReadOnlyList<EShaderType> DeclaredIn);

        private static readonly StringComparer UniformComparer = StringComparer.Ordinal;

        private static readonly Regex UniformStatementRegex = new(
            @"\buniform\b\s+(?<statement>[^;]+);",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex LayoutQualifierRegex = new(
            @"layout\s*\([^)]*\)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex SingleLineCommentRegex = new(
            @"//.*?$",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex MultiLineCommentRegex = new(
            @"/\*.*?\*/",
            RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly HashSet<string> UniformQualifiers = new(StringComparer.Ordinal)
        {
            "const",
            "flat",
            "smooth",
            "noperspective",
            "centroid",
            "sample",
            "coherent",
            "volatile",
            "restrict",
            "readonly",
            "writeonly",
            "highp",
            "mediump",
            "lowp",
            "precise",
            "patch",
            "invariant"
        };

        private static readonly Dictionary<string, EShaderVarType> UniformTypeLookup = new(StringComparer.Ordinal)
        {
            ["bool"] = EShaderVarType._bool,
            ["bvec2"] = EShaderVarType._bvec2,
            ["bvec3"] = EShaderVarType._bvec3,
            ["bvec4"] = EShaderVarType._bvec4,
            ["int"] = EShaderVarType._int,
            ["ivec2"] = EShaderVarType._ivec2,
            ["ivec3"] = EShaderVarType._ivec3,
            ["ivec4"] = EShaderVarType._ivec4,
            ["uint"] = EShaderVarType._uint,
            ["uvec2"] = EShaderVarType._uvec2,
            ["uvec3"] = EShaderVarType._uvec3,
            ["uvec4"] = EShaderVarType._uvec4,
            ["float"] = EShaderVarType._float,
            ["vec2"] = EShaderVarType._vec2,
            ["vec3"] = EShaderVarType._vec3,
            ["vec4"] = EShaderVarType._vec4,
            ["double"] = EShaderVarType._double,
            ["dvec2"] = EShaderVarType._dvec2,
            ["dvec3"] = EShaderVarType._dvec3,
            ["dvec4"] = EShaderVarType._dvec4,
            ["mat3"] = EShaderVarType._mat3,
            ["mat4"] = EShaderVarType._mat4
        };

        private readonly record struct UniformDeclaration(
            string GlslType,
            string Name,
            bool IsArray,
            int? ArrayLength,
            string? ArrayLengthExpression);

        [YamlIgnore]
        private IReadOnlyDictionary<string, ShaderUniformBinding> _cachedUniformBindings =
            new Dictionary<string, ShaderUniformBinding>(UniformComparer);

        [YamlIgnore]
        private IReadOnlyDictionary<string, ShaderTextureBinding> _cachedTextureBindings =
            new Dictionary<string, ShaderTextureBinding>(UniformComparer);

        private bool _shaderInterfaceDirty = true;

        [YamlIgnore]
        private readonly Dictionary<XRShader, ShaderSubscription> _shaderSourceSubscriptions = new();

        public event Action<XRRenderProgram>? ShaderInterfaceChanged;

        /// <summary>
        /// The shaders that make up the program.
        /// </summary>
        public EventList<XRShader> Shaders { get; } = [];

        [YamlIgnore]
        public IReadOnlyDictionary<string, ShaderUniformBinding> UniformBindings
        {
            get
            {
                EnsureShaderInterfaceMetadata();
                return _cachedUniformBindings;
            }
        }

        [YamlIgnore]
        public IReadOnlyDictionary<string, ShaderTextureBinding> TextureBindings
        {
            get
            {
                EnsureShaderInterfaceMetadata();
                return _cachedTextureBindings;
            }
        }

        private void HookShaderCollectionEvents()
        {
            Shaders.PostAnythingAdded += OnShaderAdded;
            Shaders.PostAnythingRemoved += OnShaderRemoved;
            Shaders.PostIndexSet += OnShaderIndexSet;
        }

        private void OnShaderAdded(XRShader shader)
        {
            if (shader is null)
                return;

            SubscribeToShader(shader);
        }

        private void OnShaderRemoved(XRShader shader)
        {
            if (shader is null)
                return;

            UnsubscribeFromShader(shader);
            MarkShaderInterfaceDirty();
        }

        private void OnShaderIndexSet(int index, XRShader previousShader)
        {
            if (previousShader is not null)
                UnsubscribeFromShader(previousShader);

            XRShader? current = index >= 0 && index < Shaders.Count ? Shaders[index] : null;
            if (current is not null)
                SubscribeToShader(current);

            MarkShaderInterfaceDirty();
        }

        private void SubscribeToShader(XRShader shader)
        {
            if (_shaderSourceSubscriptions.TryGetValue(shader, out var existing))
            {
                existing.ReferenceCount++;
                MarkShaderInterfaceDirty();
                return;
            }

            ShaderSubscription subscription = new()
            {
                ReferenceCount = 1
            };

            XRPropertyChangedEventHandler propertyChanged = (sender, args) =>
            {
                if (!string.Equals(args.PropertyName, nameof(XRShader.Source), StringComparison.Ordinal))
                    return;

                AttachToShaderSource(subscription, shader.Source);
                MarkShaderInterfaceDirty();
            };

            shader.PropertyChanged += propertyChanged;
            subscription.PropertyChangedHandler = propertyChanged;

            AttachToShaderSource(subscription, shader.Source);

            _shaderSourceSubscriptions[shader] = subscription;
            MarkShaderInterfaceDirty();
        }

        private void AttachToShaderSource(ShaderSubscription subscription, TextFile? source)
        {
            if (ReferenceEquals(subscription.Source, source))
                return;

            if (subscription.Source is not null && subscription.TextChangedHandler is not null)
                subscription.Source.TextChanged -= subscription.TextChangedHandler;

            subscription.Source = source;

            if (source is null)
            {
                subscription.TextChangedHandler = null;
                return;
            }

            Action handler = MarkShaderInterfaceDirty;
            source.TextChanged += handler;
            subscription.TextChangedHandler = handler;
        }

        private void UnsubscribeFromShader(XRShader shader)
        {
            if (!_shaderSourceSubscriptions.TryGetValue(shader, out var subscription))
                return;

            subscription.ReferenceCount--;
            if (subscription.ReferenceCount > 0)
                return;

            if (subscription.Source is not null && subscription.TextChangedHandler is not null)
                subscription.Source.TextChanged -= subscription.TextChangedHandler;

            if (subscription.PropertyChangedHandler is not null)
                shader.PropertyChanged -= subscription.PropertyChangedHandler;

            _shaderSourceSubscriptions.Remove(shader);
        }

        private void ClearAllShaderSubscriptions()
        {
            foreach (var (shader, subscription) in _shaderSourceSubscriptions.ToArray())
            {
                if (subscription.Source is not null && subscription.TextChangedHandler is not null)
                    subscription.Source.TextChanged -= subscription.TextChangedHandler;

                if (subscription.PropertyChangedHandler is not null)
                    shader.PropertyChanged -= subscription.PropertyChangedHandler;
            }

            _shaderSourceSubscriptions.Clear();
        }

        private void MarkShaderInterfaceDirty()
            => _shaderInterfaceDirty = true;

        protected override void OnDestroying()
        {
            ClearAllShaderSubscriptions();
            base.OnDestroying();
        }

        public void RefreshShaderInterfaceMetadata()
        {
            _shaderInterfaceDirty = true;
            EnsureShaderInterfaceMetadata();
        }

        private void EnsureShaderInterfaceMetadata()
        {
            if (!_shaderInterfaceDirty)
                return;

            RebuildShaderInterfaceMetadata();
        }

        private void RebuildShaderInterfaceMetadata()
        {
            var builder = new ShaderInterfaceBuilder();

            foreach (var shader in Shaders)
                builder.ProcessShader(shader);

            (_cachedUniformBindings, _cachedTextureBindings) = builder.Build();

            _shaderInterfaceDirty = false;

            ShaderInterfaceChanged?.Invoke(this);
        }

        public XREvent<XRRenderProgram>? LinkRequested = null;
        public XREvent<XRRenderProgram>? UseRequested = null;

        [Browsable(false)]
        public bool LinkReady { get; private set; } = false;

        private bool _separable = true;
        public bool Separable
        {
            get => _separable;
            set => SetField(ref _separable, value);
        }

        public void Use()
            => UseRequested?.Invoke(this);

        /// <summary>
        /// Call this once all shaders have been added to the Shaders list to finalize the program.
        /// </summary>
        public void AllowLink()
            => LinkReady = true;

        public void Link()
        {
            AllowLink();
            EnsureShaderInterfaceMetadata();
            LinkRequested?.Invoke(this);
        }

        public event Action<string, Matrix4x4>? UniformSetMatrix4x4Requested = null;
        public event Action<string, Quaternion>? UniformSetQuaternionRequested = null;

        public event Action<string, Matrix4x4[]>? UniformSetMatrix4x4ArrayRequested = null;
        public event Action<string, Quaternion[]>? UniformSetQuaternionArrayRequested = null;

        public event Action<string, bool>? UniformSetBoolRequested = null;
        public event Action<string, BoolVector2>? UniformSetBoolVector2Requested = null;
        public event Action<string, BoolVector3>? UniformSetBoolVector3Requested = null;
        public event Action<string, BoolVector4>? UniformSetBoolVector4Requested = null;

        public event Action<string, bool[]>? UniformSetBoolArrayRequested = null;
        public event Action<string, BoolVector2[]>? UniformSetBoolVector2ArrayRequested = null;
        public event Action<string, BoolVector3[]>? UniformSetBoolVector3ArrayRequested = null;
        public event Action<string, BoolVector4[]>? UniformSetBoolVector4ArrayRequested = null;

        public event Action<string, float>? UniformSetFloatRequested = null;
        public event Action<string, Vector2>? UniformSetVector2Requested = null;
        public event Action<string, Vector3>? UniformSetVector3Requested = null;
        public event Action<string, Vector4>? UniformSetVector4Requested = null;

        public event Action<string, float[]>? UniformSetFloatArrayRequested = null;
        public event Action<string, Span<float>> ? UniformSetFloatSpanRequested = null;
        public event Action<string, Vector2[]>? UniformSetVector2ArrayRequested = null;
        public event Action<string, Vector3[]>? UniformSetVector3ArrayRequested = null;
        public event Action<string, Vector4[]>? UniformSetVector4ArrayRequested = null;

        public event Action<string, double>? UniformSetDoubleRequested = null;
        public event Action<string, DVector2>? UniformSetDVector2Requested = null;
        public event Action<string, DVector3>? UniformSetDVector3Requested = null;
        public event Action<string, DVector4>? UniformSetDVector4Requested = null;

        public event Action<string, double[]>? UniformSetDoubleArrayRequested = null;
        public event Action<string, DVector2[]>? UniformSetDVector2ArrayRequested = null;
        public event Action<string, DVector3[]>? UniformSetDVector3ArrayRequested = null;
        public event Action<string, DVector4[]>? UniformSetDVector4ArrayRequested = null;

        public event Action<string, int>? UniformSetIntRequested = null;
        public event Action<string, IVector2>? UniformSetIVector2Requested = null;
        public event Action<string, IVector3>? UniformSetIVector3Requested = null;
        public event Action<string, IVector4>? UniformSetIVector4Requested = null;

        public event Action<string, int[]>? UniformSetIntArrayRequested = null;
        public event Action<string, IVector2[]>? UniformSetIVector2ArrayRequested = null;
        public event Action<string, IVector3[]>? UniformSetIVector3ArrayRequested = null;
        public event Action<string, IVector4[]>? UniformSetIVector4ArrayRequested = null;

        public event Action<string, uint>? UniformSetUIntRequested = null;
        public event Action<string, UVector2>? UniformSetUVector2Requested = null;
        public event Action<string, UVector3>? UniformSetUVector3Requested = null;
        public event Action<string, UVector4>? UniformSetUVector4Requested = null;

        public event Action<string, uint[]>? UniformSetUIntArrayRequested = null;
        public event Action<string, UVector2[]>? UniformSetUVector2ArrayRequested = null;
        public event Action<string, UVector3[]>? UniformSetUVector3ArrayRequested = null;
        public event Action<string, UVector4[]>? UniformSetUVector4ArrayRequested = null;

        public event Action<string, XRTexture, int>? SamplerRequested = null;
        public event Action<int, XRTexture, int>? SamplerRequestedByLocation = null;

        public event Action<uint, XRTexture, int, bool, int, EImageAccess, EImageFormat>? BindImageTextureRequested = null;
        public event Action<uint, uint, uint, IEnumerable<(uint unit, XRTexture texture, int level, int? layer, EImageAccess access, EImageFormat format)>?>? DispatchComputeRequested = null;
        public event Action<uint, XRDataBuffer>? BindBufferRequested = null;

        /// <summary>
        /// Mask of the shader types included in the program.
        /// </summary>
        public EProgramStageMask GetShaderTypeMask()
        {
            EProgramStageMask mask = EProgramStageMask.None;
            foreach (var shader in Shaders)
            {
                switch (shader.Type)
                {
                    case EShaderType.Vertex:
                        mask |= EProgramStageMask.VertexShaderBit;
                        break;
                    case EShaderType.TessControl:
                        mask |= EProgramStageMask.TessControlShaderBit;
                        break;
                    case EShaderType.TessEvaluation:
                        mask |= EProgramStageMask.TessEvaluationShaderBit;
                        break;
                    case EShaderType.Geometry:
                        mask |= EProgramStageMask.GeometryShaderBit;
                        break;
                    case EShaderType.Fragment:
                        mask |= EProgramStageMask.FragmentShaderBit;
                        break;
                    case EShaderType.Compute:
                        mask |= EProgramStageMask.ComputeShaderBit;
                        break;
                    case EShaderType.Task:
                        mask |= EProgramStageMask.TaskShaderBit;
                        break;
                    case EShaderType.Mesh:
                        mask |= EProgramStageMask.MeshShaderBit;
                        break;
                }
            }
            return mask;
        }

        public XRRenderProgram()
        {
            HookShaderCollectionEvents();
        }

        public XRRenderProgram(bool linkNow, bool separable, params XRShader[] shaders)
            : this(linkNow, separable, (IEnumerable<XRShader>)shaders) { }

        public XRRenderProgram(bool linkNow, bool separable, IEnumerable<XRShader> shaders)
            : this()
        {
            Separable = separable;
            Shaders.AddRange(shaders);
            if (linkNow)
            {
                Generate();
                Link();
            }
        }

        public IEnumerator<XRShader> GetEnumerator()
            => ((IEnumerable<XRShader>)Shaders).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator()
            => ((IEnumerable)Shaders).GetEnumerator();

        /// <summary>
        /// Sends a Matrix4x4 property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, Matrix4x4 value)
            => UniformSetMatrix4x4Requested?.Invoke(name, value);
        /// <summary>
        /// Sends a Quaternion property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, Quaternion value)
            => UniformSetQuaternionRequested?.Invoke(name, value);

        /// <summary>
        /// Sends a Matrix4x4[] property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, Matrix4x4[] value)
            => UniformSetMatrix4x4ArrayRequested?.Invoke(name, value);
        /// <summary>
        /// Sends a Quaternion[] property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, Quaternion[] value)
            => UniformSetQuaternionArrayRequested?.Invoke(name, value);

        /// <summary>
        /// Sends a bool property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, bool value)
            => UniformSetBoolRequested?.Invoke(name, value);
        /// <summary>
        /// Sends a BoolVector2 property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, BoolVector2 value)
            => UniformSetBoolVector2Requested?.Invoke(name, value);
        /// <summary>
        /// Sends a BoolVector3 property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, BoolVector3 value)
            => UniformSetBoolVector3Requested?.Invoke(name, value);
        /// <summary>
        /// Sends a BoolVector4 property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, BoolVector4 value)
            => UniformSetBoolVector4Requested?.Invoke(name, value);

        /// <summary>
        /// Sends a float property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, float value)
            => UniformSetFloatRequested?.Invoke(name, value);
        /// <summary>
        /// Sends a Vector2 property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, Vector2 value)
            => UniformSetVector2Requested?.Invoke(name, value);
        /// <summary>
        /// Sends a Vector3 property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, Vector3 value)
            => UniformSetVector3Requested?.Invoke(name, value);
        /// <summary>
        /// Sends a Vector4 property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, Vector4 value)
            => UniformSetVector4Requested?.Invoke(name, value);

        /// <summary>
        /// Sends a float[] property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, float[] value)
            => UniformSetFloatArrayRequested?.Invoke(name, value);
        /// <summary>
        /// Sends a Span&lt;float&gt; property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, Span<float> value)
            => UniformSetFloatSpanRequested?.Invoke(name, value);
        /// <summary>
        /// Sends a Vector2[] property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, Vector2[] value)
            => UniformSetVector2ArrayRequested?.Invoke(name, value);
        /// <summary>
        /// Sends a Vector3[] property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, Vector3[] value)
            => UniformSetVector3ArrayRequested?.Invoke(name, value);
        /// <summary>
        /// Sends a Vector4[] property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, Vector4[] value)
            => UniformSetVector4ArrayRequested?.Invoke(name, value);

        /// <summary>
        /// Sends a double property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, double value)
            => UniformSetDoubleRequested?.Invoke(name, value);
        /// <summary>
        /// Sends a DVector2 property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, DVector2 value)
            => UniformSetDVector2Requested?.Invoke(name, value);
        /// <summary>
        /// Sends a DVector3 property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, DVector3 value)
            => UniformSetDVector3Requested?.Invoke(name, value);
        /// <summary>
        /// Sends a DVector4 property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, DVector4 value)
            => UniformSetDVector4Requested?.Invoke(name, value);

        /// <summary>
        /// Sends a double[] property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, double[] value)
            => UniformSetDoubleArrayRequested?.Invoke(name, value);
        /// <summary>
        /// Sends a DVector2[] property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, DVector2[] value)
            => UniformSetDVector2ArrayRequested?.Invoke(name, value);
        /// <summary>
        /// Sends a DVector3[] property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, DVector3[] value)
            => UniformSetDVector3ArrayRequested?.Invoke(name, value);
        /// <summary>
        /// Sends a DVector4[] property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, DVector4[] value)
            => UniformSetDVector4ArrayRequested?.Invoke(name, value);

        /// <summary>
        /// Sends a int property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, int value)
            => UniformSetIntRequested?.Invoke(name, value);
        /// <summary>
        /// Sends a IVector2 property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, IVector2 value)
            => UniformSetIVector2Requested?.Invoke(name, value);
        /// <summary>
        /// Sends a IVector3 property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, IVector3 value)
            => UniformSetIVector3Requested?.Invoke(name, value);
        /// <summary>
        /// Sends a IVector4 property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, IVector4 value)
            => UniformSetIVector4Requested?.Invoke(name, value);

        /// <summary>
        /// Sends a int[] property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, int[] value)
            => UniformSetIntArrayRequested?.Invoke(name, value);
        /// <summary>
        /// Sends a IVector2[] property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, IVector2[] value)
            => UniformSetIVector2ArrayRequested?.Invoke(name, value);
        /// <summary>
        /// Sends a IVector3[] property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, IVector3[] value)
            => UniformSetIVector3ArrayRequested?.Invoke(name, value);
        /// <summary>
        /// Sends a IVector4[] property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, IVector4[] value)
            => UniformSetIVector4ArrayRequested?.Invoke(name, value);

        /// <summary>
        /// Sends a uint property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, uint value)
            => UniformSetUIntRequested?.Invoke(name, value);
        /// <summary>
        /// Sends a UVector2 property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, UVector2 value)
            => UniformSetUVector2Requested?.Invoke(name, value);
        /// <summary>
        /// Sends a UVector3 property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, UVector3 value)
            => UniformSetUVector3Requested?.Invoke(name, value);
        /// <summary>
        /// Sends a UVector4 property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, UVector4 value)
            => UniformSetUVector4Requested?.Invoke(name, value);

        /// <summary>
        /// Sends a uint[] property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, uint[] value)
            => UniformSetUIntArrayRequested?.Invoke(name, value);
        /// <summary>
        /// Sends a UVector2[] property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, UVector2[] value)
            => UniformSetUVector2ArrayRequested?.Invoke(name, value);
        /// <summary>
        /// Sends a UVector3[] property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, UVector3[] value)
            => UniformSetUVector3ArrayRequested?.Invoke(name, value);
        /// <summary>
        /// Sends a UVector4[] property value to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Uniform(string name, UVector4[] value)
            => UniformSetUVector4ArrayRequested?.Invoke(name, value);

        /// <summary>
        /// Sends a texture to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Sampler(string name, XRTexture texture, int textureUnit)
            => SamplerRequested?.Invoke(name, texture, textureUnit);
        /// <summary>
        /// Sends a texture to the shader program.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void Sampler(int location, XRTexture texture, int textureUnit)
            => SamplerRequestedByLocation?.Invoke(location, texture, textureUnit);

        public enum EImageAccess
        {
            ReadOnly,
            WriteOnly,
            ReadWrite
        }

        public enum EImageFormat
        {
            R8,
            R16,
            R16F,
            R32F,
            RG8,
            RG16,
            RG16F,
            RG32F,
            RGB8,
            RGB16,
            RGB16F,
            RGB32F,
            RGBA8,
            RGBA16,
            RGBA16F,
            RGBA32F,
            R8I,
            R8UI,
            R16I,
            R16UI,
            R32I,
            R32UI,
            RG8I,
            RG8UI,
            RG16I,
            RG16UI,
            RG32I,
            RG32UI,
            RGB8I,
            RGB8UI,
            RGB16I,
            RGB16UI,
            RGB32I,
            RGB32UI,
            RGBA8I,
            RGBA8UI,
            RGBA16I,
            RGBA16UI,
            RGBA32I,
            RGBA32UI
        }

        public void BindImageTexture(uint unit, XRTexture texture, int level, bool layered, int layer, EImageAccess access, EImageFormat format)
            => BindImageTextureRequested?.Invoke(unit, texture, level, layered, layer, access, format);

        /// <summary>
        /// Dispatch the program for compute.
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <param name="z"></param>
        /// <param name="textures"></param>
        public void DispatchCompute(uint x, uint y, uint z, IEnumerable<(uint unit, XRTexture texture, int level, int? layer, EImageAccess access, EImageFormat format)>? textures = null)
            => DispatchComputeRequested?.Invoke(x, y, z, textures);

        public bool HasUniform(EEngineUniform uniformName)
            => HasUniform(uniformName.ToString());
        public bool HasUniform(string uniformName)
            => Shaders.Any(x => x.HasUniform(uniformName));

        public void BindBuffer(XRDataBuffer buffer, uint location)
        {
            if (buffer is null)
                throw new ArgumentNullException(nameof(buffer), "Cannot bind a null buffer to the shader program.");
            BindBufferRequested?.Invoke(location, buffer);
        }

        /// <summary>
        /// Dispatch the program for compute and then issue a memory barrier.
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <param name="z"></param>
        /// <param name="barrierMask"></param>
        /// <param name="textures"></param>
        public void DispatchCompute(
            uint x,
            uint y,
            uint z,
            EMemoryBarrierMask barrierMask,
            IEnumerable<(uint unit, XRTexture texture, int level, int? layer, EImageAccess access, EImageFormat format)>? textures = null)
        {
            DispatchCompute(x, y, z, textures);
            AbstractRenderer.Current?.MemoryBarrier(barrierMask);
        }

        private static string StripComments(string source)
        {
            if (string.IsNullOrEmpty(source))
                return string.Empty;

            string withoutMulti = MultiLineCommentRegex.Replace(source, string.Empty);
            return SingleLineCommentRegex.Replace(withoutMulti, string.Empty);
        }

        private static IEnumerable<UniformDeclaration> ParseUniformDeclarations(string source)
        {
            if (string.IsNullOrEmpty(source))
                yield break;

            foreach (Match match in UniformStatementRegex.Matches(source))
            {
                string statement = match.Groups["statement"].Value;
                foreach (var declaration in ParseDeclarationsFromStatement(statement))
                    yield return declaration;
            }
        }

        private static List<Token> Tokenize(string statement)
        {
            List<Token> tokens = new();
            int index = 0;
            while (index < statement.Length)
            {
                if (char.IsWhiteSpace(statement[index]))
                {
                    index++;
                    continue;
                }

                int start = index;
                while (index < statement.Length && !char.IsWhiteSpace(statement[index]))
                    index++;

                tokens.Add(new Token(statement[start..index], start));
            }

            return tokens;
        }

        private static int FindTypeTokenIndex(List<Token> tokens)
        {
            for (int i = 0; i < tokens.Count; i++)
            {
                string token = tokens[i].Value.Trim();
                if (token.Length == 0)
                    continue;

                // Uniform qualifier tokens appear before the actual GLSL type.
                string bareToken = token.TrimEnd(';', ',');
                if (!UniformQualifiers.Contains(bareToken))
                    return i;
            }

            return -1;
        }

        private static IEnumerable<string> SplitDeclarators(string declarators)
        {
            if (string.IsNullOrWhiteSpace(declarators))
                yield break;

            int squareDepth = 0;
            int parenDepth = 0;
            int braceDepth = 0;
            int start = 0;

            for (int i = 0; i < declarators.Length; i++)
            {
                char c = declarators[i];
                switch (c)
                {
                    case '[':
                        squareDepth++;
                        continue;
                    case ']':
                        squareDepth = Math.Max(0, squareDepth - 1);
                        continue;
                    case '(':
                        parenDepth++;
                        continue;
                    case ')':
                        parenDepth = Math.Max(0, parenDepth - 1);
                        continue;
                    case '{':
                        braceDepth++;
                        continue;
                    case '}':
                        braceDepth = Math.Max(0, braceDepth - 1);
                        continue;
                    case ',':
                        if (squareDepth == 0 && parenDepth == 0 && braceDepth == 0)
                        {
                            if (i > start)
                                yield return declarators[start..i];
                            start = i + 1;
                        }
                        continue;
                }
            }

            if (start < declarators.Length)
                yield return declarators[start..];
        }

        private static bool TryParseDeclarator(string declarator, out string name, out bool isArray, out string? arrayExpression, out int? arrayLength)
        {
            name = string.Empty;
            isArray = false;
            arrayExpression = null;
            arrayLength = null;

            if (string.IsNullOrWhiteSpace(declarator))
                return false;

            string trimmed = declarator.Trim().TrimEnd(';');
            if (trimmed.Length == 0)
                return false;

            int equalsIndex = trimmed.IndexOf('=');
            if (equalsIndex >= 0)
                trimmed = trimmed[..equalsIndex].Trim();

            if (trimmed.Length == 0)
                return false;

            int bracketIndex = trimmed.IndexOf('[');
            if (bracketIndex >= 0)
            {
                int closingIndex = trimmed.IndexOf(']', bracketIndex + 1);
                if (closingIndex > bracketIndex)
                {
                    string expr = trimmed[(bracketIndex + 1)..closingIndex].Trim();
                    arrayExpression = string.IsNullOrWhiteSpace(expr) ? null : expr;
                    isArray = true;
                }

                name = trimmed[..bracketIndex].Trim();
            }
            else
            {
                name = trimmed.Trim().TrimEnd(',');
            }

            if (string.IsNullOrWhiteSpace(name))
                return false;

            if (isArray && arrayExpression is not null &&
                int.TryParse(arrayExpression, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLength) && parsedLength > 0)
            {
                arrayLength = parsedLength;
            }

            return true;
        }

        private static IEnumerable<UniformDeclaration> ParseDeclarationsFromStatement(string statement)
        {
            if (string.IsNullOrWhiteSpace(statement))
                yield break;

            if (statement.Contains('{'))
                yield break; // skip blocks such as uniform buffers or nested struct definitions

            statement = LayoutQualifierRegex.Replace(statement, string.Empty);
            statement = statement.Trim().TrimEnd(';');
            if (statement.Length == 0)
                yield break;

            var tokens = Tokenize(statement);
            if (tokens.Count == 0)
                yield break;

            int typeIndex = FindTypeTokenIndex(tokens);
            if (typeIndex == -1)
                yield break;

            var typeToken = tokens[typeIndex];
            string glslType = typeToken.Value;
            int declaratorStart = typeToken.Position + glslType.Length;
            if (declaratorStart >= statement.Length)
                yield break;

            string declarators = statement[declaratorStart..].Trim();
            if (declarators.Length == 0)
                yield break;

            foreach (string rawDeclarator in SplitDeclarators(declarators))
            {
                if (!TryParseDeclarator(rawDeclarator, out string name, out bool isArray, out string? arrayExpr, out int? arrayLength))
                    continue;

                yield return new UniformDeclaration(glslType, name, isArray, arrayLength, arrayExpr);
            }
        }

        private static bool IsTextureType(string? glslType)
        {
            if (string.IsNullOrWhiteSpace(glslType))
                return false;

            return glslType.IndexOf("sampler", StringComparison.OrdinalIgnoreCase) >= 0
                || glslType.IndexOf("image", StringComparison.OrdinalIgnoreCase) >= 0
                || glslType.StartsWith("subpassInput", StringComparison.OrdinalIgnoreCase);
        }

        private static EShaderVarType? TryResolveEngineType(string glslType)
            => UniformTypeLookup.TryGetValue(glslType, out var type) ? type : null;

        private static IReadOnlyDictionary<string, StructDefinition> ParseStructDefinitions(string source)
        {
            Dictionary<string, StructDefinition> definitions = new(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(source))
                return definitions;

            int index = 0;
            while (index < source.Length)
            {
                int structIndex = source.IndexOf("struct", index, StringComparison.Ordinal);
                if (structIndex == -1)
                    break;

                bool validPrefix = structIndex == 0 || !IsIdentifierChar(source[structIndex - 1]);
                if (!validPrefix)
                {
                    index = structIndex + 6;
                    continue;
                }

                int nameStart = structIndex + 6;
                while (nameStart < source.Length && char.IsWhiteSpace(source[nameStart]))
                    nameStart++;

                int nameEnd = nameStart;
                while (nameEnd < source.Length && IsIdentifierChar(source[nameEnd]))
                    nameEnd++;

                if (nameEnd == nameStart)
                {
                    index = structIndex + 6;
                    continue;
                }

                string structName = source[nameStart..nameEnd];

                int braceStart = source.IndexOf('{', nameEnd);
                if (braceStart == -1)
                    break;

                int braceEnd = FindMatchingBrace(source, braceStart);
                if (braceEnd == -1)
                    break;

                string body = source[(braceStart + 1)..braceEnd];
                var fields = ParseStructFields(body).ToList();
                definitions[structName] = new StructDefinition(structName, fields);

                index = braceEnd + 1;
            }

            return definitions;
        }

        private static IEnumerable<UniformDeclaration> ParseStructFields(string body)
        {
            foreach (string statement in SplitStatements(body))
            {
                foreach (var declaration in ParseDeclarationsFromStatement(statement))
                    yield return declaration;
            }
        }

        private static IEnumerable<string> SplitStatements(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
                yield break;

            int squareDepth = 0;
            int parenDepth = 0;
            int braceDepth = 0;
            int start = 0;

            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];
                switch (c)
                {
                    case '[':
                        squareDepth++;
                        break;
                    case ']':
                        squareDepth = Math.Max(0, squareDepth - 1);
                        break;
                    case '(':
                        parenDepth++;
                        break;
                    case ')':
                        parenDepth = Math.Max(0, parenDepth - 1);
                        break;
                    case '{':
                        braceDepth++;
                        break;
                    case '}':
                        braceDepth = Math.Max(0, braceDepth - 1);
                        break;
                    case ';':
                        if (squareDepth == 0 && parenDepth == 0 && braceDepth == 0)
                        {
                            if (i >= start)
                            {
                                string statement = body[start..i].Trim();
                                if (statement.Length > 0)
                                    yield return statement;
                            }
                            start = i + 1;
                        }
                        break;
                }
            }

            if (start < body.Length)
            {
                string tail = body[start..].Trim();
                if (tail.Length > 0)
                    yield return tail;
            }
        }

        private static int FindMatchingBrace(string source, int openingIndex)
        {
            int depth = 0;
            for (int i = openingIndex; i < source.Length; i++)
            {
                char c = source[i];
                if (c == '{')
                {
                    depth++;
                    continue;
                }

                if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                        return i;
                }
            }

            return -1;
        }

        private static bool IsIdentifierChar(char c)
            => char.IsLetterOrDigit(c) || c == '_';

        private static IEnumerable<UniformDeclaration> ExpandDeclaration(
            UniformDeclaration declaration,
            IReadOnlyDictionary<string, StructDefinition> structDefinitions)
        {
            var recursionGuard = new HashSet<string>(StringComparer.Ordinal);
            foreach (var expanded in ExpandDeclarationInternal(declaration, structDefinitions, null, recursionGuard))
                yield return expanded;
        }

        private static IEnumerable<UniformDeclaration> ExpandDeclarationInternal(
            UniformDeclaration declaration,
            IReadOnlyDictionary<string, StructDefinition> structDefinitions,
            string? parentPrefix,
            HashSet<string> recursionGuard)
        {
            string fullName = string.IsNullOrEmpty(parentPrefix)
                ? declaration.Name
                : string.Concat(parentPrefix, '.', declaration.Name);

            yield return declaration with { Name = fullName };

            if (declaration.IsArray)
                yield break; // Cannot safely expand arrayed structs without knowing indices.

            if (!structDefinitions.TryGetValue(declaration.GlslType, out var structDefinition))
                yield break;

            if (!recursionGuard.Add(structDefinition.Name))
                yield break;

            foreach (var field in structDefinition.Fields)
            {
                foreach (var nested in ExpandDeclarationInternal(field, structDefinitions, fullName, recursionGuard))
                    yield return nested;
            }

            recursionGuard.Remove(structDefinition.Name);
        }

        private static string GetShaderDisplayName(XRShader shader)
        {
            if (shader is null)
                return "<null>";

            if (!string.IsNullOrWhiteSpace(shader.Name))
                return shader.Name;

            if (!string.IsNullOrWhiteSpace(shader.Source?.FilePath))
                return shader.Source.FilePath;

            return shader.Type.ToString();
        }

        private readonly record struct Token(string Value, int Position);
        private sealed record StructDefinition(string Name, IReadOnlyList<UniformDeclaration> Fields);

        private sealed class ShaderSubscription
        {
            public TextFile? Source;
            public Action? TextChangedHandler;
            public XRPropertyChangedEventHandler? PropertyChangedHandler;
            public int ReferenceCount;
        }

        private sealed class ShaderInterfaceBuilder
        {
            private readonly Dictionary<string, UniformAccumulator> _uniforms = new(UniformComparer);
            private readonly Dictionary<string, TextureAccumulator> _textures = new(UniformComparer);

            public void ProcessShader(XRShader shader)
            {
                if (shader is null)
                    return;

                string? source = shader.Source?.Text;
                if (string.IsNullOrWhiteSpace(source))
                    return;

                string sanitized = StripComments(source);
                var structDefinitions = ParseStructDefinitions(sanitized);
                foreach (var declaration in ParseUniformDeclarations(sanitized))
                {
                    foreach (var expanded in ExpandDeclaration(declaration, structDefinitions))
                    {
                        if (IsTextureType(expanded.GlslType))
                            AddTexture(expanded, shader);
                        else
                            AddUniform(expanded, shader);
                    }
                }
            }

            public (IReadOnlyDictionary<string, ShaderUniformBinding> uniforms, IReadOnlyDictionary<string, ShaderTextureBinding> textures) Build()
            {
                var builtUniforms = new Dictionary<string, ShaderUniformBinding>(_uniforms.Count, UniformComparer);
                foreach (var (name, accumulator) in _uniforms)
                {
                    builtUniforms[name] = new ShaderUniformBinding(
                        name,
                        accumulator.GlslType,
                        accumulator.EngineType,
                        accumulator.IsArray,
                        accumulator.ArrayLength,
                        accumulator.ArrayExpression,
                        accumulator.GetStages());
                }

                var builtTextures = new Dictionary<string, ShaderTextureBinding>(_textures.Count, UniformComparer);
                foreach (var (name, accumulator) in _textures)
                {
                    builtTextures[name] = new ShaderTextureBinding(
                        name,
                        accumulator.GlslType,
                        accumulator.IsArray,
                        accumulator.ArrayLength,
                        accumulator.ArrayExpression,
                        accumulator.GetStages());
                }

                return (builtUniforms, builtTextures);
            }

            private void AddUniform(UniformDeclaration declaration, XRShader shader)
            {
                if (!_uniforms.TryGetValue(declaration.Name, out var accumulator))
                {
                    accumulator = new UniformAccumulator(
                        declaration.GlslType,
                        TryResolveEngineType(declaration.GlslType),
                        declaration.IsArray,
                        declaration.ArrayLength,
                        declaration.ArrayLengthExpression);
                    _uniforms[declaration.Name] = accumulator;
                }
                else if (!accumulator.Merge(declaration))
                {
                    Debug.LogWarning($"Uniform '{declaration.Name}' has conflicting declarations in shader '{GetShaderDisplayName(shader)}'.");
                }

                accumulator.RegisterStage(shader.Type);
            }

            private void AddTexture(UniformDeclaration declaration, XRShader shader)
            {
                if (!_textures.TryGetValue(declaration.Name, out var accumulator))
                {
                    accumulator = new TextureAccumulator(
                        declaration.GlslType,
                        declaration.IsArray,
                        declaration.ArrayLength,
                        declaration.ArrayLengthExpression);
                    _textures[declaration.Name] = accumulator;
                }
                else if (!accumulator.Merge(declaration))
                {
                    Debug.LogWarning($"Texture '{declaration.Name}' has conflicting declarations in shader '{GetShaderDisplayName(shader)}'.");
                }

                accumulator.RegisterStage(shader.Type);
            }

            private sealed class UniformAccumulator
            {
                public UniformAccumulator(string glslType, EShaderVarType? engineType, bool isArray, int? arrayLength, string? arrayExpression)
                {
                    GlslType = glslType;
                    EngineType = engineType;
                    IsArray = isArray;
                    ArrayLength = arrayLength;
                    ArrayExpression = arrayExpression;
                }

                public string GlslType { get; }
                public EShaderVarType? EngineType { get; }
                public bool IsArray { get; }
                public int? ArrayLength { get; }
                public string? ArrayExpression { get; private set; }
                private readonly HashSet<EShaderType> _stages = new();

                public void RegisterStage(EShaderType stage)
                    => _stages.Add(stage);

                public IReadOnlyList<EShaderType> GetStages()
                    => _stages.OrderBy(static s => (int)s).ToArray();

                public bool Merge(UniformDeclaration declaration)
                {
                    if (!string.Equals(GlslType, declaration.GlslType, StringComparison.Ordinal))
                        return false;
                    if (IsArray != declaration.IsArray)
                        return false;
                    if (IsArray && ArrayLength.HasValue && declaration.ArrayLength.HasValue && ArrayLength.Value != declaration.ArrayLength.Value)
                        return false;

                    if (ArrayExpression is null && declaration.ArrayLengthExpression is not null)
                        ArrayExpression = declaration.ArrayLengthExpression;

                    return true;
                }
            }

            private sealed class TextureAccumulator
            {
                public TextureAccumulator(string glslType, bool isArray, int? arrayLength, string? arrayExpression)
                {
                    GlslType = glslType;
                    IsArray = isArray;
                    ArrayLength = arrayLength;
                    ArrayExpression = arrayExpression;
                }

                public string GlslType { get; }
                public bool IsArray { get; }
                public int? ArrayLength { get; }
                public string? ArrayExpression { get; private set; }
                private readonly HashSet<EShaderType> _stages = new();

                public void RegisterStage(EShaderType stage)
                    => _stages.Add(stage);

                public IReadOnlyList<EShaderType> GetStages()
                    => _stages.OrderBy(static s => (int)s).ToArray();

                public bool Merge(UniformDeclaration declaration)
                {
                    if (!string.Equals(GlslType, declaration.GlslType, StringComparison.Ordinal))
                        return false;
                    if (IsArray != declaration.IsArray)
                        return false;
                    if (IsArray && ArrayLength.HasValue && declaration.ArrayLength.HasValue && ArrayLength.Value != declaration.ArrayLength.Value)
                        return false;

                    if (ArrayExpression is null && declaration.ArrayLengthExpression is not null)
                        ArrayExpression = declaration.ArrayLengthExpression;

                    return true;
                }
            }
        }

        /// <summary>
        /// Utility for computing GPU compute dispatch group dimensions.
        /// </summary>
        public static class ComputeDispatch
        {
            public static (uint x, uint y, uint z) ForCommands(uint commandCount, uint localSizeX = 256, uint maxGroupsPerDim = 65535)
            {
                // Total workgroups needed (ceil)
                ulong groupsNeeded = (commandCount + (localSizeX - 1)) / localSizeX;

                // Distribute across X/Y/Z with per-dimension cap, using ceil at each step.
                ulong gx = Math.Min(groupsNeeded, (ulong)maxGroupsPerDim);
                ulong remainingAfterX = (groupsNeeded + gx - 1) / gx;

                ulong gy = Math.Min(remainingAfterX, (ulong)maxGroupsPerDim);
                ulong remainingAfterY = (remainingAfterX + gy - 1) / gy;

                ulong gz = Math.Min(remainingAfterY, (ulong)maxGroupsPerDim);

                // Sanity: if even after packing we still need more than Max in Z, N is astronomically large.
                if (gx * gy * gz < groupsNeeded)
                    throw new ArgumentOutOfRangeException(nameof(commandCount),
                        $"Too many commands for single dispatch under per-dimension limit {maxGroupsPerDim}.");

                return ((uint)gx, (uint)gy, (uint)gz);
            }
        }
    }
}
