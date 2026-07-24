//using XREngine.Rendering.Shaders.Generator;

//namespace XREngine.Rendering.Models.Materials.Functions
//{
//    public abstract class ResultFunc : ShaderMethod
//    {
//        private XRMaterial _material;
//        public XRMaterial Material
//        {
//            get => _material;
//            set
//            {
//                _material = value;
//            }
//        }

//        public ResultFunc() : base(false)
//        {
//            HasGlobalVarDec = true;
//            ReturnsInline = false;
//        }

//        public bool Generate(out XRShader[] shaderFiles, out ShaderVar[] shaderVars) 
//            => ShaderGeneratorBase.Generate(this, out shaderFiles, out shaderVars);
//    }
//}