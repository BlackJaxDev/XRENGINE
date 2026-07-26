//using XREngine.Rendering.UI.Functions;

//namespace XREngine.Rendering.Models.Materials.Functions
//{
//    [FunctionDefinition(
//        "Interpolation",
//        "Hermite",
//        "Smoothly interpolates between Start and End using a value in between:\n" +
//        "t = clamp((Value - Start) / (End - Start), 0.0, 1.0);\n" +
//        "t * t * (3.0 - 2.0 * t)",
//        "hermite smoothstep interpolate interpolation blend")]
//    public class HermiteFunc : ShaderMethod
//    {
//        public HermiteFunc() : base(true, EShaderStageFlag.All) { }
//        protected override string GetOperation() => "smoothstep({0}, {1}, {2})";
//        public override void GetDefinition(out string[] inputNames, out string[] outputNames, out MatFuncOverload[] overloads)
//        {
//            inputNames = new string[] { "Start", "End", "Value" };
//            outputNames = new string[] { string.Empty };
//            overloads = new MatFuncOverload[]
//            {
//                new MatFuncOverload(EGLSLVersion.Ver_130, EGenShaderVarType.GenFloat,
//                    new EGenShaderVarType[]
//                    {
//                        EGenShaderVarType.GenFloat,
//                        EGenShaderVarType.GenFloat,
//                        EGenShaderVarType.GenFloat
//                    }),
//                new MatFuncOverload(EGLSLVersion.Ver_130, EGenShaderVarType.GenFloat,
//                    new EGenShaderVarType[]
//                    {
//                        EGenShaderVarType.Float,
//                        EGenShaderVarType.Float,
//                        EGenShaderVarType.GenFloat
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
//                        EGenShaderVarType.Double,
//                        EGenShaderVarType.Double,
//                        EGenShaderVarType.GenDouble
//                    }),
//            };
//        }
//    }
//}
