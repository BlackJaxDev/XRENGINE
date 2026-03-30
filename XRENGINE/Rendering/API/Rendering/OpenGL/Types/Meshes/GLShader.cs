using System.IO;
using Silk.NET.OpenGL;
using XREngine.Data.Core;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        public class GLShader(OpenGLRenderer renderer, XRShader data) : GLObject<XRShader>(renderer, data)
        {
            public const string EXT_GL_OVR_MULTIVIEW2 = "GL_OVR_multiview2";
            public const string EXT_GL_EXT_MULTIVIEW = "GL_EXT_multiview";
            public const string EXT_GL_NV_STEREO_VIEW_RENDERING = "GL_NV_stereo_view_rendering";
            
            private bool _isCompiled = false;
            private bool _compilePending;
            private bool _compileAsSeparable;

            /// <summary>
            /// GL_COMPLETION_STATUS_ARB (0x91B1) — used to poll non-blocking compile/link when
            /// GL_ARB_parallel_shader_compile is available.
            /// </summary>
            internal const int GL_COMPLETION_STATUS_ARB = 0x91B1;

            protected override void UnlinkData()
            {
                Data.PropertyChanged -= Data_PropertyChanged;
            }

            protected override void LinkData()
            {
                Data.PropertyChanged += Data_PropertyChanged;
                OnSourceChanged();
                // Set the include directory path from the source file's directory
                UpdateLocalIncludeDirectory();
            }

            private void UpdateLocalIncludeDirectory()
            {
                string? filePath = Data.Source?.FilePath;
                if (!string.IsNullOrEmpty(filePath))
                {
                    LocalIncludeDirectoryPath = Path.GetDirectoryName(filePath);
                }
            }

            private void Data_PropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
            {
                switch (e.PropertyName)
                {
                    case nameof(XRShader.Source):
                        OnSourceChanged();
                        break;
                    case nameof(XRShader.Type):
                        //Have to regenerate a new shader with the new type
                        Destroy();
                        break;
                }
            }

            public override EGLObjectType Type => EGLObjectType.Shader;
            
            public event Action? SourceChanged;

            public string? SourceText => Data.Source;
            public EShaderType Mode => Data.Type;

            public string? LocalIncludeDirectoryPath { get; set; } = null;

            public bool IsCompiled
            {
                get => _isCompiled;
                set => SetField(ref _isCompiled, value);
            }

            /// <summary>
            /// True when an async compile has been dispatched but completion has not yet been confirmed.
            /// Only meaningful when GL_ARB_parallel_shader_compile is active.
            /// </summary>
            public bool IsCompilePending => _compilePending;

            public void PrepareCompileVariant(bool separableProgram)
                => _compileAsSeparable = separableProgram;

            /// <summary>
            /// Polls the driver for async shader-compilation completion (GL_ARB_parallel_shader_compile).
            /// Returns <c>true</c> when compilation is resolved (check <see cref="IsCompiled"/> for success/failure).
            /// Returns <c>false</c> if the shader is still being compiled by the driver.
            /// </summary>
            public bool PollCompileCompletion()
            {
                if (!_compilePending)
                    return true;

                Api.GetShader(BindingId, (GLEnum)GL_COMPLETION_STATUS_ARB, out int complete);
                if (complete == 0)
                    return false; // Still compiling

                // Compilation finished — query actual result
                _compilePending = false;
                Api.GetShader(BindingId, GLEnum.CompileStatus, out int status);
                IsCompiled = status != 0;

                if (!IsCompiled)
                {
                    Api.GetShaderInfoLog(BindingId, out string? info);
                    if (!string.IsNullOrEmpty(info))
                        Debug.OpenGLWarning(info);
                }

                return true;
            }

            public EventList<GLRenderProgram> ActivePrograms { get; } = [];

            private static ShaderType ToGLEnum(EShaderType mode)
                => mode switch
                {
                    EShaderType.Vertex => ShaderType.VertexShader,
                    EShaderType.Fragment => ShaderType.FragmentShader,
                    EShaderType.Geometry => ShaderType.GeometryShader,
                    EShaderType.TessControl => ShaderType.TessControlShader,
                    EShaderType.TessEvaluation => ShaderType.TessEvaluationShader,
                    EShaderType.Compute => ShaderType.ComputeShader,
                    EShaderType.Task => (ShaderType)0x955A,
                    EShaderType.Mesh => (ShaderType)0x9559,
                    _ => ShaderType.FragmentShader
                };

            private void OnSourceChanged()
            {
                IsCompiled = false;
                UpdateLocalIncludeDirectory();
                if (IsGenerated)
                    PushSource();
                SourceChanged?.Invoke();
            }

            protected internal override void PreGenerated() { }
            protected internal override void PostGenerated()
                => PushSource();

            protected override uint CreateObject()
                => Api.CreateShader(ToGLEnum(Mode));

            private void PushSource(bool compile = true)
            {
                if (!Engine.IsRenderThread)
                {
                    Engine.EnqueueMainThreadTask(() => PushSource(compile));
                    return;
                }

                if (string.IsNullOrWhiteSpace(SourceText))
                    return;

                string? trueScript = ResolveFullSource();
                if (trueScript is null)
                {
                    Debug.OpenGLWarning("Shader source is null after resolving includes.");
                    return;
                }

                Api.ShaderSource(BindingId, trueScript);
                if (compile && !Compile(out _) && !_compilePending)
                    Debug.OpenGLWarning(GetFullSource(true));
            }

            public string GetFullSource(bool lineNumbers)
            {
                string? source = string.Empty;
                string? trueScript = ResolveFullSource();
                if (lineNumbers)
                {
                    //Split the source by new lines
                    string[]? s = trueScript?.Split([Environment.NewLine], StringSplitOptions.None);

                    //Add the line number to the source so we can go right to errors on specific lines
                    if (s != null)
                        for (int i = 0; i < s.Length; i++)
                            source += $"{(i + 1).ToString().PadLeft(s.Length.ToString().Length, '0')}: {s[i] ?? string.Empty} {Environment.NewLine}";
                }
                else
                    source += trueScript + Environment.NewLine;
                return source;
            }

            /// <summary>
            /// Compiles the shader with debug information.
            /// When GL_ARB_parallel_shader_compile is active, the call is non-blocking:
            /// <see cref="IsCompilePending"/> will be <c>true</c> and callers must poll via
            /// <see cref="PollCompileCompletion"/>.
            /// </summary>
            public bool Compile(out string? info, bool printLogInfo = true)
            {
                Api.CompileShader(BindingId);

                if (Engine.Rendering.State.HasParallelShaderCompile)
                {
                    _compilePending = true;
                    info = null;
                    return false; // Compilation in progress — caller must poll
                }

                Api.GetShader(BindingId, GLEnum.CompileStatus, out int status);
                Api.GetShaderInfoLog(BindingId, out info);
                IsCompiled = status != 0;
                if (printLogInfo)
                {
                    if (!string.IsNullOrEmpty(info))
                        Debug.OpenGLWarning(info);
                    else if (!IsCompiled)
                        Debug.OpenGLWarning("Unable to compile shader, but no error was returned.");
                }
                return IsCompiled;
            }

            /// <summary>
            /// Compiles the shader.
            /// When GL_ARB_parallel_shader_compile is active, the call is non-blocking.
            /// </summary>
            public bool Compile()
            {
                Api.CompileShader(BindingId);

                if (Engine.Rendering.State.HasParallelShaderCompile)
                {
                    _compilePending = true;
                    return false;
                }

                Api.GetShader(BindingId, GLEnum.CompileStatus, out int status);
                IsCompiled = status != 0;
                return IsCompiled;
            }

            public string? ResolveFullSource()
            {
                Data.TryGetResolvedSource(out string src);

                return GLShaderSourceCompatibility.InjectMissingGLPerVertexBlocks(src, Mode, RequiresSeparableCompatibility());
            }

            private bool RequiresSeparableCompatibility()
            {
                if (_compileAsSeparable)
                    return true;

                foreach (GLRenderProgram program in ActivePrograms)
                {
                    if (program.Data.Separable)
                        return true;
                }

                return false;
            }
        }

        //public ShaderType CurrentShaderMode { get; private set; } = ShaderType.FragmentShader;
    }
}