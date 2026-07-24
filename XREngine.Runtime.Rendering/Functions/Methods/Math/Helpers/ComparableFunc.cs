//namespace XREngine.Rendering.Models.Materials.Functions
//{
//    public abstract class ComparableFunc : ShaderMethod
//    {
//        public ComparableFunc() : base(true) { }
//        protected virtual string GetInputAName() => "A";
//        protected virtual string GetInputBName() => "B";
//        protected override string GetOperation()
//            => CurrentValidOverloads[0] < 4 ? "({0} " + GetScalarOperator() + " {1})" : GetVectorFuncName() + "({0}, {1})";
//        protected abstract string GetScalarOperator();
//        protected abstract string GetVectorFuncName();
//        public override void GetDefinition(out string[] inputNames, out string[] outputNames, out MatFuncOverload[] overloads)
//        {
//            inputNames = new string[] { GetInputAName(), GetInputBName() };
//            outputNames = new string[] { string.Empty };
//            overloads = new MatFuncOverload[]
//            {
//                new MatFuncOverload(EGLSLVersion.Ver_110, EGenShaderVarType.Bool,
//                    new EGenShaderVarType[] { EGenShaderVarType.Float, EGenShaderVarType.Float }),
//                new MatFuncOverload(EGLSLVersion.Ver_110, EGenShaderVarType.Bool,
//                    new EGenShaderVarType[] { EGenShaderVarType.Double, EGenShaderVarType.Double }),
//                new MatFuncOverload(EGLSLVersion.Ver_110, EGenShaderVarType.Bool,
//                    new EGenShaderVarType[] { EGenShaderVarType.Int, EGenShaderVarType.Int }),
//                new MatFuncOverload(EGLSLVersion.Ver_110, EGenShaderVarType.Bool,
//                    new EGenShaderVarType[] { EGenShaderVarType.Uint, EGenShaderVarType.Uint }),

//                new MatFuncOverload(EGLSLVersion.Ver_110, EGenShaderVarType.VecBool,
//                    new EGenShaderVarType[] { EGenShaderVarType.VecFloat, EGenShaderVarType.VecFloat }),
//                //new Overload(EGLSLVersion.Ver_110, EGenShaderVarType.VecBool,
//                //    new EGenShaderVarType[] { EGenShaderVarType.VecDouble, EGenShaderVarType.VecDouble }),
//                new MatFuncOverload(EGLSLVersion.Ver_110, EGenShaderVarType.VecBool,
//                    new EGenShaderVarType[] { EGenShaderVarType.VecInt, EGenShaderVarType.VecInt }),
//                new MatFuncOverload(EGLSLVersion.Ver_110, EGenShaderVarType.VecBool,
//                    new EGenShaderVarType[] { EGenShaderVarType.VecUint, EGenShaderVarType.VecUint }),
//            };
//        }
//    }
//}
