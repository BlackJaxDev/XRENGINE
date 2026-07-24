//using XREngine.Rendering.UI.Functions;

//namespace XREngine.Rendering.Models.Materials.Functions
//{
//    [FunctionDefinition(
//        OperatorCategoryName,
//        "Pow",
//        "Returns A to the power of B.",
//        "exponent ^ power to raise")]
//    public class PowFunc : ShaderMethod
//    {
//        public PowFunc() : base() { }
//        protected override string GetOperation() => "pow({0}, {1})";
//        public override void GetDefinition(out string[] inputNames, out string[] outputNames, out MatFuncOverload[] overloads)
//        {
//            inputNames = new string[] { "Base", "Power" };
//            outputNames = new string[] { string.Empty };
//            overloads = new MatFuncOverload[]
//            {
//                new MatFuncOverload(EGLSLVersion.Ver_110, EGenShaderVarType.GenFloat, 
//                    new EGenShaderVarType[] { EGenShaderVarType.GenFloat, EGenShaderVarType.GenFloat }),
//            };
//        }
//    }
//}
