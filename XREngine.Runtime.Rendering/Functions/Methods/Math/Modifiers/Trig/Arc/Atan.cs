//using XREngine.Rendering.UI.Functions;

//namespace XREngine.Rendering.Models.Materials.Functions
//{
//    [FunctionDefinition(
//        TrigCategoryName,
//        "Atan",
//        "Returns the arctangent of the given value.", 
//        "arc tangent value inverse trigonometry")]
//    public class AtanFunc : BasicGenFloatFunc
//    {
//        public AtanFunc() : base() { }
//        protected override string GetFuncName() => "atan";
//        protected override string GetInputName() => "Tan";
//        protected override string GetOutputName() => "Rad";
//        protected override EGLSLVersion GetVersion() => EGLSLVersion.Ver_110;
//    }
//    [FunctionDefinition(
//       TrigCategoryName,
//       "Atan2",
//       "Returns the arctangent of Y / X.",
//       "arc tangent value inverse trigonometry")]
//    public class Atan2Func : ShaderMethod
//    {
//        public Atan2Func() : base() { }
//        protected override string GetOperation() => "atan({0}, {1})";
//        public override void GetDefinition(out string[] inputNames, out string[] outputNames, out MatFuncOverload[] overloads)
//        {
//            inputNames = new string[] { "Y", "X" };
//            outputNames = new string[] { "Rad" };
//            overloads = new MatFuncOverload[]
//            {
//                new MatFuncOverload(EGLSLVersion.Ver_110, EGenShaderVarType.GenFloat, 
//                    new EGenShaderVarType[] { EGenShaderVarType.GenFloat, EGenShaderVarType.GenFloat }),
//            };
//        }
//    }
//}
