//using XREngine.Rendering.UI.Functions;

//namespace XREngine.Rendering.Models.Materials.Functions
//{
//    /// <summary>
//    /// returns the absolute value of the input value
//    /// </summary>
//    [FunctionDefinition(
//        OperatorCategoryName,
//        "Negate",
//        "Returns 0 - value.",
//        "negate negative zero - value minus")]
//    public class NegateFunc : BasicCustomOverloadFunc
//    {
//        public NegateFunc() : base() { }
//        protected override string GetFuncName() => "-";
//        protected override string GetOperation() => "-{0}";
//        protected override MatFuncOverload[] GetInOutOverloads()
//            => new MatFuncOverload[]
//            {
//                new MatFuncOverload(EGLSLVersion.Ver_110, EGenShaderVarType.GenFloat),
//                new MatFuncOverload(EGLSLVersion.Ver_110, EGenShaderVarType.GenDouble),
//                new MatFuncOverload(EGLSLVersion.Ver_110, EGenShaderVarType.GenInt),
//            };
//    }
//}
