using Extensions;
using Silk.NET.OpenGL;
using XREngine.Data.Core;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        public class GLShader(OpenGLRenderer renderer, XRShader data) : GLObject<XRShader>(renderer, data)
        {
            public const string EXT_GL_OVR_MULTIVIEW2 = "GL_OVR_multiview2";
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

            public override GLObjectType Type => GLObjectType.Shader;
            
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
                    _ => ShaderType.FragmentShader
                };

            private void OnSourceChanged()
            {
                IsCompiled = false;
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
                    Debug.LogWarning("Shader source is null after resolving includes.");
                    return;
                }

                Api.ShaderSource(BindingId, trueScript);
                if (compile && !Compile(out _))
                    Debug.LogWarning(GetFullSource(true));
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
                        Debug.LogWarning(info);
                    else if (!IsCompiled)
                        Debug.LogWarning("Unable to compile shader, but no error was returned.");
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
                List<string?> resolvedPaths = [];
                string? src = ResolveIncludesRecursive(SourceText, resolvedPaths) ?? SourceText;

                if (resolvedPaths.Count > 0)
                    Debug.Out($"Resolved {resolvedPaths.Count} includes:{Environment.NewLine}{src}");
                
                return src;
            }

            /// <summary>
            /// Searches for '#include' directives in the source text and replaces them with the contents of the included file.
            /// </summary>
            /// <param name="sourceText"></param>
            /// <param name="resolvedPaths"></param>
            /// <returns></returns>
            private string? ResolveIncludesRecursive(string? sourceText, List<string?> resolvedPaths)
            {
                if (string.IsNullOrEmpty(sourceText))
                    return null;

                int[] includeLocations = sourceText.FindAllOccurrences(0, "#include");
                (int Index, int Length, string InsertText)[] insertions = new (int, int, string)[includeLocations.Length];
                for (int i = 0; i < includeLocations.Length; ++i)
                {
                    int loc = includeLocations[i];
                    int pathIndex = loc + 8;
                    while (char.IsWhiteSpace(sourceText[pathIndex])) ++pathIndex;
                    char first = sourceText[pathIndex];
                    int endIndex;
                    int startIndex;
                    if (first == '"')
                    {
                        //Quoted path
                        startIndex = pathIndex + 1;
                        endIndex = sourceText.FindFirst(pathIndex + 1, '"');
                    }
                    else
                    {
                        //Unquoted path, use newline to determine end
                        startIndex = pathIndex;
                        static bool IsNewline(char x) => x == '\n';
                        endIndex = sourceText.FindFirst(pathIndex + 1, IsNewline);
                    }
                    string fileText;
                    string includePath = sourceText[startIndex..endIndex];
                    if (string.IsNullOrWhiteSpace(includePath))
                        fileText = string.Empty;
                    else
                    {
                        try
                        {
                            if (!includePath.IsAbsolutePath())
                            {
                                bool valid = false;
                                string? fullPath = null;
                                string?[] dirCheckPaths = 
                                [
                                    LocalIncludeDirectoryPath,
                                    //File?.DirectoryPath,
                                    //Engine.Game?.DirectoryPath
                                ];
                                foreach (string? dirPath in dirCheckPaths)
                                {
                                    if (!string.IsNullOrWhiteSpace(dirPath))
                                    {
                                        fullPath = Path.Combine(dirPath, includePath);
                                        valid = File.Exists(fullPath);
                                        if (valid)
                                            break;
                                    }
                                }
                                includePath = !valid ? Path.GetFullPath(includePath) : fullPath ?? string.Empty;
                            }
                            if (resolvedPaths.Contains(includePath))
                            {
                                //Infinite recursion, path already visited
                                Debug.Out($"Infinite include recursion detected; the path '{includePath}' will not be included again.");
                                fileText = string.Empty;
                            }
                            else
                            {
                                fileText = File.ReadAllText(includePath);
                                resolvedPaths.Add(includePath);
                                fileText = ResolveIncludesRecursive(fileText, resolvedPaths) ?? string.Empty;
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.Out(ex.Message);
                            fileText = string.Empty;
                        }
                    }
                    insertions[i] = (loc, endIndex + 1 - loc, fileText);
                }

                int offset = 0;
                int index;
                foreach (var (Index, Length, InsertText) in insertions)
                {
                    index = Index + offset;
                    sourceText = sourceText.Remove(index, Length);
                    sourceText = sourceText.Insert(index, InsertText);
                    offset += InsertText.Length - Length;
                }

                return sourceText;
            }
        }

        //public ShaderType CurrentShaderMode { get; private set; } = ShaderType.FragmentShader;
    }
}