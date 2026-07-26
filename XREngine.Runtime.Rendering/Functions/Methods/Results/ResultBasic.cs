//using XREngine.Rendering.UI.Functions;

//namespace XREngine.Rendering.Models.Materials.Functions
//{
//    /// <summary>
//    /// Basic rendering result.
//    /// </summary>
//    [FunctionDefinition(
//        "Output",
//        "Basic Output [Forward]",
//        "Outputs the given Vector4 color as the color for this fragment in a forward shading pipeline.",
//        "result output final return")]
//    public class ResultBasicFunc : ResultFunc
//    {
//        public ResultBasicFunc() : base() { }
//        protected override string GetOperation()
//            => "OutColor = Vector4({0}, {1})";
//        public override string GetGlobalVarDec()
//            => "layout(location = 0) out Vector4 OutColor;";
//        public override void GetDefinition(out string[] inputNames, out string[] outputNames, out MatFuncOverload[] overloads)
//        {
//            inputNames = new string[] { "Color", "Opacity", "World Position Offset" };
//            outputNames = new string[] { };
//            overloads = new MatFuncOverload[]
//            {
//                new MatFuncOverload(EGLSLVersion.Ver_110, new EGenShaderVarType[]
//                {
//                    EGenShaderVarType.Vector3,
//                    EGenShaderVarType.Float,
//                    EGenShaderVarType.Vector3,
//                }, true)
//            };
//        }
//    }
//}