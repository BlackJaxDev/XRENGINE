using System.IO;
using Silk.NET.OpenGL;
using XREngine.Data.Core;
using XREngine.Rendering.Shaders;

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
                if (compile && !Compile(out _))
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
            /// </summary>
            /// <param name="info"></param>
            /// <param name="printLogInfo"></param>
            /// <returns></returns>
            public bool Compile(out string? info, bool printLogInfo = true)
            {
                Api.CompileShader(BindingId);
                Api.GetShader(BindingId, GLEnum.CompileStatus, out int status);
                Api.GetShaderInfoLog(BindingId, out info);
                IsCompiled = status != 0;
                if (printLogInfo)
                {
                    if (!string.IsNullOrEmpty(info))
                        Debug.OpenGLWarning(info);
                    else if (!IsCompiled)
                        Debug.OpenGLWarning("Unable to compile shader, but no error was returned.");
                    //else
                    //    Debug.Out("Shader compiled successfully.");
                }
                return IsCompiled;
            }

            /// <summary>
            /// Compiles the shader.
            /// </summary>
            /// <returns></returns>
            public bool Compile()
            {
                Api.CompileShader(BindingId);
                Api.GetShader(BindingId, GLEnum.CompileStatus, out int status);
                IsCompiled = status != 0;
                return IsCompiled;
            }

            public string? ResolveFullSource()
            {
                string? src = ShaderSourcePreprocessor.ResolveSource(SourceText ?? string.Empty, Data.Source?.FilePath, out List<string> resolvedPaths);

                if (resolvedPaths.Count > 0)
                    Debug.OpenGL($"Resolved {resolvedPaths.Count} includes:{Environment.NewLine}{string.Join(Environment.NewLine, resolvedPaths.Select(x => $" - {x}"))}");
                
                return src;
            }
        }

        //public ShaderType CurrentShaderMode { get; private set; } = ShaderType.FragmentShader;
    }
}