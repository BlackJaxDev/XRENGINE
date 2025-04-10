using System.Globalization;
using System.Numerics;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Shaders.Generator
{
    /// <summary>
    /// Base class for generating shaders.
    /// </summary>
    /// <param name="mesh"></param>
    public abstract class ShaderGeneratorBase(XRMesh mesh)
    {
        private const EGLSLVersion GLSLCurrentVersion = EGLSLVersion.Ver_460;
        private readonly string NewLine = Environment.NewLine;

        private string _shaderCode = "";
        private int _tabCount = 0;

        public XRMesh Mesh { get; } = mesh;

        public abstract string Generate();

        #region String Helpers
        private string Tabs
        {
            get
            {
                string t = "";
                for (int i = 0; i < _tabCount; i++)
                    t += "\t";
                return t;
            }
        }
        public void Reset()
        {
            _shaderCode = "";
            _tabCount = 0;
        }

        public void WriteVersion(EGLSLVersion version)
            => Line($"#version {version.ToString()[4..]}");
        public void WriteVersion()
            => WriteVersion(GLSLCurrentVersion);
        public void WriteInVar(uint layoutLocation, EShaderVarType type, string name)
            => Line($"layout (location = {layoutLocation}) in {type.ToString()[1..]} {name};");
        public void WriteInVar(EShaderVarType type, string name)
            => Line($"in {type.ToString()[1..]} {name};");

        public void WriteOutVar(int layoutLocation, EShaderVarType type, string name)
            => Line($"layout (location = {layoutLocation}) out {type.ToString()[1..]} {name};");
        public void WriteOutVar(EShaderVarType type, string name)
            => Line($"out {type.ToString()[1..]} {name};");

        public void WriteUniform(int layoutLocation, EShaderVarType type, string name, bool array = false)
            => Line($"layout (location = {layoutLocation}) uniform {type.ToString()[1..]} {name}{(array ? "[]" : "")};");

        public void WriteUniform(EShaderVarType type, string name, bool array = false)
            => Line($"{(_inBlock ? string.Empty : "uniform ")}{type.ToString()[1..]} {name}{(array ? "[]" : "")};");

        private static string Format(int defaultValue)
            => defaultValue.ToString(CultureInfo.InvariantCulture);
        private static string Format(float defaultValue)
            => defaultValue.ToString("0.0###f", CultureInfo.InvariantCulture);
        private static string Format(Vector2 defaultValue)
            => $"vec2({defaultValue.X}, {defaultValue.Y})";
        private static string Format(Vector3 defaultValue)
            => $"vec3({defaultValue.X}, {defaultValue.Y}, {defaultValue.Z})";
        private static string Format(Vector4 defaultValue)
            => $"vec4({defaultValue.X}, {defaultValue.Y}, {defaultValue.Z}, {defaultValue.W})";

        public void WriteUniform(int layoutLocation, EShaderVarType type, string name, float defaultValue, bool array = false)
            => Line($"layout (location = {layoutLocation}) uniform {type.ToString()[1..]} {name}{(array ? "[]" : "")} = {Format(defaultValue)};");

        public void WriteUniform(EShaderVarType type, string name, float defaultValue, bool array = false)
            => Line($"{(_inBlock ? string.Empty : "uniform ")}{type.ToString()[1..]} {name}{(array ? "[]" : "")} = {Format(defaultValue)};");

        public void WriteUniform(int layoutLocation, EShaderVarType type, string name, Vector2 defaultValue, bool array = false)
            => Line($"layout (location = {layoutLocation}) uniform {type.ToString()[1..]} {name}{(array ? "[]" : "")} = {Format(defaultValue)};");

        public void WriteUniform(EShaderVarType type, string name, Vector2 defaultValue, bool array = false)
            => Line($"{(_inBlock ? string.Empty : "uniform ")}{type.ToString()[1..]} {name}{(array ? "[]" : "")} = {Format(defaultValue)};");

        public void WriteUniform(int layoutLocation, EShaderVarType type, string name, Vector3 defaultValue, bool array = false)
            => Line($"layout (location = {layoutLocation}) uniform {type.ToString()[1..]} {name}{(array ? "[]" : "")} = {Format(defaultValue)};");

        public void WriteUniform(EShaderVarType type, string name, Vector3 defaultValue, bool array = false)
            => Line($"{(_inBlock ? string.Empty : "uniform ")}{type.ToString()[1..]} {name}{(array ? "[]" : "")} = {Format(defaultValue)};");

        public void WriteUniform(int layoutLocation, EShaderVarType type, string name, Vector4 defaultValue, bool array = false)
            => Line($"layout (location = {layoutLocation}) uniform {type.ToString()[1..]} {name}{(array ? "[]" : "")} = {Format(defaultValue)};");

        public void WriteUniform(EShaderVarType type, string name, Vector4 defaultValue, bool array = false)
            => Line($"{(_inBlock ? string.Empty : "uniform ")}{type.ToString()[1..]} {name}{(array ? "[]" : "")} = {Format(defaultValue)};");

        public void WriteUniform(int layoutLocation, EShaderVarType type, string name, int defaultValue, bool array = false)
            => Line($"layout (location = {layoutLocation}) uniform {type.ToString()[1..]} {name}{(array ? "[]" : "")} = {Format(defaultValue)};");

        public void WriteUniform(EShaderVarType type, string name, int defaultValue, bool array = false)
            => Line($"{(_inBlock ? string.Empty : "uniform ")}{type.ToString()[1..]} {name}{(array ? "[]" : "")} = {Format(defaultValue)};");

        public StateObject StartShaderStorageBufferBlock(string bufferName, int binding)
        {
            _inBlock = true;
            Line($"layout(std430, binding = {binding}) buffer {bufferName}");
            OpenBracket();
            return StateObject.New(EndBufferBlock);
        }
        public StateObject StartUniformBufferBlock(string bufferName, int binding)
        {
            _inBlock = true;
            Line($"layout(binding = {binding}) uniform {bufferName}");
            OpenBracket();
            return StateObject.New(EndBufferBlock);
        }
        public void EndBufferBlock()
        {
            CloseBracket(null, true);
            _inBlock = false;
        }

        private bool _inBlock = false;
        public StateObject StartUniformBlock(string structName, string variableName)
        {
            _inBlock = true;
            Line($"uniform {structName}");
            OpenBracket();
            return StateObject.New(() => EndUniformBlock(variableName));
        }
        public void StartUniformBlock(string structName)
        {
            _inBlock = true;
            Line($"uniform {structName}");
            OpenBracket();
        }
        public void EndUniformBlock(string? variableName = null)
        {
            CloseBracket(variableName, true);
            _inBlock = false;
        }
        public void Comment(string comment, params object[] args)
        {
            Line($"//{comment}");
        }
        public void OpenLoop(int startIndex, int count, string varName = "i")
        {
            Line($"for (int {varName} = {startIndex}; {varName} < {count}; ++{varName})");
            OpenBracket();
        }
        public void OpenLoop(int count, string varName = "i") => OpenLoop(0, count, varName);
        public StateObject StartMain()
        {
            Line("void main()");
            return OpenBracketState();
        }
        public string End()
        {
            string s = _shaderCode;
            Reset();
            //if (Mesh.HasBlendshapes && Mesh.UtilizedBones.Length > 0)
            //    Debug.Out(s);
            return s;
        }
        protected void IfElse(string condition, Action ifCode, Action elseCode)
        {
            Line($"if ({condition})");
            using (OpenBracketState())
                ifCode();
            Line("else");
            using (OpenBracketState())
                elseCode();
        }
        protected void If(string condition, Action code)
        {
            Line($"if ({condition})");
            using (OpenBracketState())
                code();
        }
        protected void MultiIf(params (string condition, Action code)[] conditions)
        {
            for (int i = 0; i < conditions.Length; i++)
            {
                Line($"if ({conditions[i].condition})");
                using (OpenBracketState())
                    conditions[i].code();
            }
        }
        protected void MultiIfElse(Action elseCode, params (string condition, Action code)[] conditions)
        {
            MultiIf(conditions);
            Line("else");
            using (OpenBracketState())
                elseCode();
        }
        public enum EGLVertexShaderInput
        {
            gl_VertexID, //in int
            gl_InstanceID, //in int
            gl_DrawID, //in int - Requires GLSL 4.60 or ARB_shader_draw_parameters
            gl_BaseVertex, //in int - Requires GLSL 4.60 or ARB_shader_draw_parameters
            gl_BaseInstance, //in int - Requires GLSL 4.60 or ARB_shader_draw_parameters
        }
        public enum EGLVertexShaderOutput //out gl_PerVertex
        {
            gl_Position, //vec4
            gl_PointSize, //float
            gl_ClipDistance, //float
        }
        public enum EGLFragmentShaderInput
        {
            gl_FragCoord, //in vec4
            gl_FrontFacing, //in bool
            gl_PointCoord, //in vec2
            gl_SampleID, //in int
            gl_SamplePosition, //in vec2
            gl_SampleMaskIn, //in int
            gl_ClipDistance, //in float
            gl_PrimitiveID, //in int
            gl_Layer, //in int - Requires GL 4.3
            gl_ViewportIndex, //in int - Requires GL 4.3
        }
        public enum EGLFragmentShaderOutput
        {
            gl_FragDepth, //out float
            gl_SampleMask, //out int
        }
        public enum ETessControlShaderInput
        {
            /// <summary>
            /// The number of vertices in the input patch.
            /// </summary>
            gl_PatchVerticesIn,
            /// <summary>
            /// The index of the current patch within this rendering command.
            /// </summary>
            gl_PrimitiveID,
            /// <summary>
            /// The index of the TCS invocation within this patch.
            /// A TCS invocation writes to per-vertex output variables by using this to index them.
            /// </summary>
            gl_InvocationID,
        }
        public enum ETessControlShaderOutput
        {
            gl_TessLevelOuter, //patch out float[4]
            gl_TessLevelInner, //patch out float[2]
        }
        public enum ETessControlShaderInputPerVertex //in gl_PerVertex
        {
            gl_Position, //vec4
            gl_PointSize, //float
            gl_ClipDistance, //float[]
        } // gl_in[gl_MaxPatchVertices];
        public enum ETessControlShaderOutputPerVertex //out gl_PerVertex
        {
            gl_Position, //vec4
            gl_PointSize, //float
            gl_ClipDistance, //float[]
        } // gl_out[];
        public enum ETessEvalShaderInput
        {
            gl_TessCoord, //in vec3
            gl_PatchVerticesIn, //in int
            gl_PrimitiveID, //in int
            gl_TessLevelOuter, //patch in float[4]
            gl_TessLevelInner, //patch in float[2]
        }
        public enum ETessEvalShaderInputPerVertex //in gl_PerVertex
        {
            gl_Position, //vec4
            gl_PointSize, //float
            gl_ClipDistance, //float[]
        } // gl_in[gl_MaxPatchVertices];
        public enum ETessEvalShaderOutputPerVertex //out gl_PerVertex
        {
            /// <summary>
            /// The clip-space output position of the current vertex.
            /// </summary>
            gl_Position, //vec4
            /// <summary>
            /// The pixel width/height of the point being rasterized.
            /// It only has a meaning when rendering point primitives, which in a TES requires using the point_mode​ input layout qualifier.
            /// </summary>
            gl_PointSize, //float
            /// <summary>
            /// Allows the shader to set the distance from the vertex to each User-Defined Clip Plane. 
            /// A positive distance means that the vertex is inside/behind the clip plane, and a negative distance means it is outside/in front of the clip plane.
            /// Each element in the array is one clip plane.
            /// In order to use this variable, the user must manually redeclare it with an explicit size.
            /// </summary>
            gl_ClipDistance, //float[]
        }
        public enum EComputeShaderInput
        {
            /// <summary>
            /// This variable contains the number of work groups passed to the dispatch function.
            /// </summary>
            gl_NumWorkGroups, //in uvec3
            /// <summary>
            /// This is the current work group for this shader invocation. Each of the XYZ components will be on the half-open range [0, gl_NumWorkGroups.XYZ).
            /// </summary>
            gl_WorkGroupID, //in uvec3
            /// <summary>
            /// This is the current invocation of the shader within the work group. Each of the XYZ components will be on the half-open range [0, gl_WorkGroupSize.XYZ).
            /// </summary>
            gl_LocalInvocationID, //in uvec3
            /// <summary>
            /// This value uniquely identifies this particular invocation of the compute shader among all invocations of this compute dispatch call. It's a short-hand for the math computation:
            /// </summary>
            gl_GlobalInvocationID, //in uvec3
            /// <summary>
            /// This is a 1D version of gl_LocalInvocationID. It identifies this invocation's index within the work group. It is short-hand for this math computation:
            /// gl_LocalInvocationIndex =
            /// gl_LocalInvocationID.z * gl_WorkGroupSize.x * gl_WorkGroupSize.y +
            /// gl_LocalInvocationID.y * gl_WorkGroupSize.x + 
            /// gl_LocalInvocationID.x;
            /// </summary>
            gl_LocalInvocationIndex, //in uint
            /// <summary>
            /// The gl_WorkGroupSize variable is a constant that contains the local work-group size of the shader, in 3 dimensions. It is defined by the layout qualifiers local_size_x/y/z. This is a compile-time constant. 
            /// </summary>
            gl_WorkGroupSize, //const uvec3 - GLSL ≥ 4.30
        }
        public enum EGeometryShaderInputPerVertex //in gl_PerVertex
        {
            gl_Position, //vec4
            gl_PointSize, //float
            gl_ClipDistance, //float[]
        } //gl_in[];
        public enum EGeometryShaderOutputPerVertex //out gl_PerVertex
        {
            gl_Position, //vec4
            gl_PointSize, //float
            gl_ClipDistance, //float[]
        }
        public enum EGeometryShaderOutput
        {
            gl_PrimitiveID, //out int
            gl_Layer, //out int
            gl_ViewportIndex, //out int - Requires GL 4.1 or ARB_viewport_array.
        }
        protected void WriteGLPerVertexOut()
        {
            using (StartOutStructState("gl_PerVertex"))
            {
                Declare(EShaderVarType._vec4, "gl_Position");
                Declare(EShaderVarType._float, "gl_PointSize");
                Declare(EShaderVarType._float, "gl_ClipDistance[]");
            }
            Line();
        }

        public StateObject StartOutStructState(string structName)
        {
            Line($"out {structName}");
            return OpenBracketState(null, true);
        }
        public void Declare(EShaderVarType varType, string varName)
            => Line($"{varType.ToString()[1..]} {varName};");
        /// <summary>
        /// Writes the current line and increments to the next line.
        /// Do not use arguments if you need to include brackets in the string.
        /// </summary>
        public void Line(string str = "")
        {
            str += NewLine;

            //Decrease tabs for every close bracket
            //if (args.Length == 0)
                _tabCount -= str.Count(x => x == '}');

            bool s = false;
            int r = str.LastIndexOf(NewLine);
            if (r == str.Length - NewLine.Length)
            {
                str = str[..^NewLine.Length];
                s = true;
            }
            str = str.Replace(NewLine, NewLine + Tabs);
            if (s)
                str += NewLine;

            _shaderCode += Tabs + str;

            //Increase tabs for every open bracket
            //if (args.Length == 0)
                _tabCount += str.Count(x => x == '{');
        }
        public void OpenBracket()
        {
            Line("{");
        }
        public StateObject OpenBracketState(string? closeBracketName = null, bool closeBracketIncludeSemicolon = false)
        {
            Line("{");
            return StateObject.New(() => CloseBracket(closeBracketName, closeBracketIncludeSemicolon));
        }
        public void CloseBracket(string? name = null, bool includeSemicolon = false)
        {
            if (!string.IsNullOrWhiteSpace(name))
                Line($"}} {name}{(includeSemicolon ? ";" : string.Empty)}");
            else
                Line(includeSemicolon ? "};" : "}");
        }

        public enum ShaderVarType
        {
            Vec2,
            Vec3,
            Vec4,
            Mat3,
            Mat4,
            Float,
            Int
        }

        public StateObject WriteLoopStart(string varName, int start, int end)
        {
            Line($"for (int {varName} = {start}; {varName} < {end}; {varName}++)");
            return OpenBracketState();
        }

        public StateObject WriteLoopStart(string varName, int end)
            => WriteLoopStart(varName, 0, end);

        public void AssignVariable(string varName, string value)
            => Line($"{varName} = {value};");
        public void AddToVariable(string varName, string value)
            => Line($"{varName} += {value};");
        public void DeclareVariable(ShaderVarType type, string varName, string initialValue = "")
        {
            string typeString = type switch
            {
                ShaderVarType.Vec2 => "vec2",
                ShaderVarType.Vec3 => "vec3",
                ShaderVarType.Vec4 => "vec4",
                ShaderVarType.Mat3 => "mat3",
                ShaderVarType.Mat4 => "mat4",
                ShaderVarType.Float => "float",
                ShaderVarType.Int => "int",
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
            };

            Line(string.IsNullOrEmpty(initialValue) 
                ? $"{typeString} {varName};" 
                : $"{typeString} {varName} = {initialValue};");
        }
        #endregion

        //public static bool Generate(
        //    ResultFunc resultFunction,
        //    out Shader[]? shaderFiles,
        //    out ShaderVar[]? shaderVars)
        //{
        //    if (resultFunction is null)
        //    {
        //        shaderFiles = null;
        //        shaderVars = null;
        //        return false;
        //    }

        //    List<ShaderVar> vars = [];
        //    MaterialGenerator fragGen = new();

        //    SortedDictionary<int, List<MaterialFunction>> deepness = new()
        //    {
        //        { 0, new List<MaterialFunction>() }
        //    };

        //    VarNameGen nameGen = new();
        //    HashSet<MeshParam> meshParamUsage = [];
        //    HashSet<EEngineUniform> engineParamUsage = [];
        //    List<string> globalDeclarations = [];

        //    EGLSLVersion maxGLSLVer = EGLSLVersion.Ver_110;
        //    Prepass(resultFunction, nameGen, 0, deepness, fragGen, meshParamUsage, engineParamUsage, globalDeclarations, ref maxGLSLVer);

        //    fragGen.WriteVersion();
        //    foreach (string p in globalDeclarations)
        //        fragGen.Line(p);
        //    foreach (MeshParam p in meshParamUsage)
        //        fragGen.Line(p.GetVariableInDeclaration());
        //    foreach (EEngineUniform p in engineParamUsage)
        //        fragGen.WriteUniform(EngineParameterFunc.GetUniformType(p), p.ToString());

        //    fragGen.StartMain();

        //    var funcLists = deepness.OrderByDescending(x => x.Key).Select(x => x.Value).ToArray();
        //    HashSet<MaterialFunction> written = [];
        //    foreach (var list in funcLists)
        //        foreach (var func in list)
        //            if (written.Add(func))
        //            {
        //                if (func is ShaderMethod method)
        //                {
        //                    string syntax = method.GetCodeSyntax(out bool returnsInline);
        //                    if (returnsInline)
        //                    {
        //                        string type = method.OutputArguments[0].ArgumentType.ToString()[1..];
        //                        string name = method.GetOutputNames()[0];
        //                        fragGen.Line(string.Format("{0} {1} = {2};", type, name, syntax));
        //                    }
        //                    else
        //                    {
        //                        fragGen.Line(syntax);
        //                    }
        //                }
        //                else
        //                {
        //                    throw new Exception();
        //                }
        //            }

        //    string fragStr = fragGen.EndMain();

        //    Debug.Out($"Generated Fragment Shader, {maxGLSLVer}:{Environment.NewLine}{fragStr}");

        //    shaderFiles =
        //    [
        //        new(ShaderType.FragmentShader, fragStr),
        //    ];
        //    shaderVars = [.. vars];

        //    return true;
        //}

        //private static void Prepass(
        //    MaterialFunction func, 
        //    VarNameGen nameGen, 
        //    int deepness,
        //    SortedDictionary<int, List<MaterialFunction>> deepnessDic,
        //    MaterialGenerator fragGen,
        //    HashSet<MeshParam> meshParamUsage,
        //    HashSet<EEngineUniform> engineParamUsage,
        //    List<string> globalDeclarations,
        //    ref EGLSLVersion maxGLSLVer)
        //{
        //    deepnessDic[deepness++].Add(func);
        //    if (func is ShaderMethod method)
        //    {
        //        if (method.HasGlobalVarDec)
        //        {
        //            string s = method.GetGlobalVarDec();
        //            if (!string.IsNullOrWhiteSpace(s))
        //                globalDeclarations.Add(s);
        //        }

        //        foreach (MeshParam p in method.NecessaryMeshParams)
        //            meshParamUsage.Add(p);

        //        foreach (EEngineUniform p in method.NecessaryEngineParams)
        //            engineParamUsage.Add(p);

        //        //maxGLSLVer = (EGLSLVersion)Math.Max((int)maxGLSLVer, (int)method.Overloads[method.CurrentValidOverloads[0]].Version);
        //    }

        //    foreach (MatFuncValueOutput output in func.OutputArguments)
        //        if (output.Connections.Count > 0)
        //            output.OutputVarName = nameGen.New();

        //    foreach (MatFuncValueInput input in func.InputArguments)
        //        if (input.Connection != null)
        //        {
        //            if (!deepnessDic.ContainsKey(deepness))
        //                deepnessDic.Add(deepness, []);

        //            Prepass(
        //                input.Connection.ParentSocket,
        //                nameGen, deepness, deepnessDic, fragGen, 
        //                meshParamUsage, engineParamUsage, globalDeclarations,
        //                ref maxGLSLVer);
        //        }
        //}
        //public sealed class MatNode
        //{
        //    public MaterialFunction? Func { get; set; }
        //    public string[]? OutputNames { get; set; }
        //    public MatNode[]? Children { get; set; }
        //    public int Deepness { get; set; } = 0;
        //}
    }
}
