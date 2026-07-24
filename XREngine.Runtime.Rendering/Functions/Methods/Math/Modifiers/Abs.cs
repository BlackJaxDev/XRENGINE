//using XREngine.Rendering.UI.Functions;

//namespace XREngine.Rendering.Models.Materials.Functions
//{
//    [FunctionDefinition(
//        ModifierCategoryName,
//        "Abs",
//        "Returns the absolute value of the given value; |value|", 
//        "absolute value")]
//    public class AbsFunc : BasicCustomOverloadFunc
//    {
//        public AbsFunc() : base() { }
//        protected override string GetFuncName() => "abs";
//        protected override MatFuncOverload[] GetInOutOverloads()
//            => new MatFuncOverload[]
//            {
//                new MatFuncOverload(EGLSLVersion.Ver_110, EGenShaderVarType.GenFloat),
//                new MatFuncOverload(EGLSLVersion.Ver_130, EGenShaderVarType.GenInt),
//                new MatFuncOverload(EGLSLVersion.Ver_410, EGenShaderVarType.GenDouble),
//            };
//    }
//}
