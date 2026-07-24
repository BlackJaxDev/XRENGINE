//using XREngine.Rendering.UI.Functions;

//namespace XREngine.Rendering.Models.Materials.Functions
//{
//    /// <summary>
//    /// 1.0f - input
//    /// </summary>
//    [FunctionDefinition(
//        ModifierCategoryName,
//        "One Minus",
//        "Returns 1 - value.",
//        "one minus value 1 - subtract negative")]
//    public class OneMinusFunc : BasicCustomOverloadFunc
//    {
//        public OneMinusFunc() : base() { }
//        protected override string GetFuncName() => null;
//        protected override string GetOperation()
//            => One(InputArguments[0].ArgumentType) + " - {0}";
//        protected override MatFuncOverload[] GetInOutOverloads()
//            => new MatFuncOverload[]
//            {
//                new MatFuncOverload(EGLSLVersion.Ver_110, EGenShaderVarType.GenFloat),
//                new MatFuncOverload(EGLSLVersion.Ver_110, EGenShaderVarType.GenDouble),
//                new MatFuncOverload(EGLSLVersion.Ver_110, EGenShaderVarType.GenInt),
//            };
//    }
//}
