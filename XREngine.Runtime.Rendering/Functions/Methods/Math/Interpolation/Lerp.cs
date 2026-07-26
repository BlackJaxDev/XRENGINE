//using XREngine.Rendering.UI.Functions;

//namespace XREngine.Rendering.Models.Materials.Functions
//{
//    [FunctionDefinition(
//        "Interpolation",
//        "Lerp",
//        "Linearly interpolates between Start and End using Time: Start + (End - Start) * Time",
//        "lerp mix linear interpolate interpolation blend")]
//    public class LerpFunc : ShaderMethod
//    {
//        public LerpFunc() : base(true) { }
//        protected override string GetOperation() => "mix({0}, {1}, {2})";
//        public override void GetDefinition(out string[] inputNames, out string[] outputNames, out MatFuncOverload[] overloads)
//        {
//            inputNames = new string[] { "Start", "End", "Time" };
//            outputNames = new string[] { string.Empty };
//            overloads = new MatFuncOverload[]
//            {
//                new MatFuncOverload(EGLSLVersion.Ver_110,  EGenShaderVarType.GenFloat,
//                    new EGenShaderVarType[]
//                    {
//                        EGenShaderVarType.GenFloat,
//                        EGenShaderVarType.GenFloat,
//                        EGenShaderVarType.GenFloat
//                    }),
//                new MatFuncOverload(EGLSLVersion.Ver_110, EGenShaderVarType.GenFloat,
//                    new EGenShaderVarType[]
//                    {
//                        EGenShaderVarType.GenFloat,
//                        EGenShaderVarType.GenFloat,
//                        EGenShaderVarType.Float
//                    }),
//                new MatFuncOverload(EGLSLVersion.Ver_400, EGenShaderVarType.GenDouble,
//                    new EGenShaderVarType[]
//                    {
//                        EGenShaderVarType.GenDouble,
//                        EGenShaderVarType.GenDouble,
//                        EGenShaderVarType.GenDouble
//                    }),
//                new MatFuncOverload(EGLSLVersion.Ver_400, EGenShaderVarType.GenDouble,
//                    new EGenShaderVarType[]
//                    {
//                        EGenShaderVarType.GenDouble,
//                        EGenShaderVarType.GenDouble,
//                        EGenShaderVarType.Double
//                    }),
//                new MatFuncOverload(EGLSLVersion.Ver_110, EGenShaderVarType.GenFloat,
//                    new EGenShaderVarType[]
//                    {
//                        EGenShaderVarType.GenFloat,
//                        EGenShaderVarType.GenFloat,
//                        EGenShaderVarType.GenBool
//                    }),
//                new MatFuncOverload(EGLSLVersion.Ver_400, EGenShaderVarType.GenDouble,
//                    new EGenShaderVarType[]
//                    {
//                        EGenShaderVarType.GenDouble,
//                        EGenShaderVarType.GenDouble,
//                        EGenShaderVarType.GenBool
//                    }),
//                new MatFuncOverload(EGLSLVersion.Ver_450, EGenShaderVarType.GenInt,
//                    new EGenShaderVarType[]
//                    {
//                        EGenShaderVarType.GenInt,
//                        EGenShaderVarType.GenInt,
//                        EGenShaderVarType.GenBool
//                    }),
//                new MatFuncOverload(EGLSLVersion.Ver_450, EGenShaderVarType.GenUInt,
//                    new EGenShaderVarType[]
//                    {
//                        EGenShaderVarType.GenUInt,
//                        EGenShaderVarType.GenUInt,
//                        EGenShaderVarType.GenBool
//                    }),
//                new MatFuncOverload(EGLSLVersion.Ver_450, EGenShaderVarType.GenBool,
//                    new EGenShaderVarType[]
//                    {
//                        EGenShaderVarType.GenBool,
//                        EGenShaderVarType.GenBool,
//                        EGenShaderVarType.GenBool
//                    }),
//            };
//        }
//    }
//}
