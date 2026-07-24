//using XREngine.Rendering.UI.Functions;

//namespace XREngine.Rendering.Models.Materials.Functions
//{
//    /// <summary>
//    /// Physically-based rendering result.
//    /// </summary>
//    [FunctionDefinition(
//        "Output",
//        "PBR Output [Deferred]",
//        "Combines the given inputs using a deferred physically-based shading pipeline.",
//        "result output final return physically based rendering PBR albedo roughness shininess specularity metallic refraction")]
//    public class ResultPBRFunc : ResultFunc
//    {
//        public ResultPBRFunc() : base()
//        {
//            NecessaryMeshParams.Add(new MeshParam(EMeshValue.FragNorm, 0));
//        }
//        public override string GetGlobalVarDec()
//        {
//            return @"
//layout(location = 0) out Vector4 AlbedoOpacity;
//layout(location = 1) out Vector3 Normal;
//layout(location = 2) out Vector4 RMSI;";
//        }

//        protected override string GetOperation()
//        {
//            return @"
//AlbedoOpacity = Vector4({0}, {1});
//Normal = {2};
//RMSI = Vector4({3}, {4}, {5}, {6});";
//        }

//        public override void GetDefinition(out string[] inputNames, out string[] outputNames, out MatFuncOverload[] overloads)
//        {
//            inputNames = new string[] { "Albedo", "Opacity", "World Normal", "Roughness", "Metallic", "Specularity", "Refraction", "World Position Offset" };
//            outputNames = new string[] { };
//            overloads = new MatFuncOverload[]
//            {
//                new MatFuncOverload(EGLSLVersion.Ver_110, new EGenShaderVarType[]
//                {
//                    EGenShaderVarType.Vector3,
//                    EGenShaderVarType.Float,
//                    EGenShaderVarType.Vector3,
//                    EGenShaderVarType.Float,
//                    EGenShaderVarType.Float,
//                    EGenShaderVarType.Float,
//                    EGenShaderVarType.Float,
//                    EGenShaderVarType.Vector3,
//                }, true)
//            };
//        }
//    }
//}
