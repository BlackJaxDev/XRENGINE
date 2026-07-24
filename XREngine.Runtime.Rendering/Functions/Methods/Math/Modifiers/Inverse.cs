//using XREngine.Rendering.UI.Functions;

//namespace XREngine.Rendering.Models.Materials.Functions
//{
//    [FunctionDefinition(
//        ModifierCategoryName,
//        "Inverse",
//        "Returns 1 / value.",
//        "inverse divided divison one 1 over value")]
//    public class InverseFunc : BasicCustomOverloadFunc
//    {
//        public InverseFunc() : base() { }
//        protected override string GetFuncName() => null;
//        protected override string GetOperation()
//            => One(InputArguments[0].ArgumentType) + " / {0}";
//        protected override MatFuncOverload[] GetInOutOverloads()
//            => new MatFuncOverload[]
//            {
//                new MatFuncOverload(EGLSLVersion.Ver_110, EGenShaderVarType.GenFloat),
//                new MatFuncOverload(EGLSLVersion.Ver_110, EGenShaderVarType.GenDouble),
//            };
//    }
//}
