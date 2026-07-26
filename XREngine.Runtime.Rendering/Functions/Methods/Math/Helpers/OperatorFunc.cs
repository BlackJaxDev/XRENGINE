//namespace XREngine.Rendering.Models.Materials.Functions
//{
//    public abstract class OperatorFunc : ShaderMethod
//    {
//        public OperatorFunc() : base(true) { }
//        protected virtual string GetInputAName() => "A";
//        protected virtual string GetInputBName() => "B";
//        protected override string GetOperation() => "({0} " + GetOperator() + " {1})";
//        protected abstract string GetOperator();
//        public override void GetDefinition(out string[] inputNames, out string[] outputNames, out MatFuncOverload[] overloads)
//        {
//            inputNames = new string[] { GetInputAName(), GetInputBName() };
//            outputNames = new string[] { string.Empty };
//            overloads = new MatFuncOverload[]
//            {
//                new MatFuncOverload(EGLSLVersion.Ver_110, EGenShaderVarType.GenFloat),
//                new MatFuncOverload(EGLSLVersion.Ver_110, EGenShaderVarType.GenDouble),
//                new MatFuncOverload(EGLSLVersion.Ver_110, EGenShaderVarType.GenInt),
//                new MatFuncOverload(EGLSLVersion.Ver_110, EGenShaderVarType.GenUInt),
//            };
//        }
//    }
//}
