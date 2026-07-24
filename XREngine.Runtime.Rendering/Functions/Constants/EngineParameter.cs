//using XREngine.Rendering.UI.Functions;

//namespace XREngine.Rendering.Models.Materials.Functions
//{
//    [FunctionDefinition(
//                "Constants",
//                "Engine Parameter",
//                "Provides a commom engine parameter value to the shader.",
//                "constant scalar parameter")]
//    public class EngineParameterFunc : ShaderMethod
//    {
//        public EngineParameterFunc() : this(EEngineUniform.UpdateDelta) { }
//        public EngineParameterFunc(EEngineUniform value) : base() => Value = value;
        
//        private EEngineUniform _value;
//        private EShaderVarType Type
//        {
//            get => (EShaderVarType)OutputArguments[0].CurrentArgumentType;
//            set
//            {
//                Overloads[0].Outputs[0] = (EGenShaderVarType)value;
//                //OutputArguments[0].AllowedArgumentTypes = new int[] { (int)value };
//            }
//        }

//        public EEngineUniform Value
//        {
//            get => _value;
//            set
//            {
//                Type = GetUniformType(_value = value);
//                _headerString.Text = _value.ToString();
//                ArrangeControls();
//                NecessaryEngineParams.Clear();
//                NecessaryEngineParams.Add(_value);
//            }
//        }
//        public static EShaderVarType GetUniformType(EEngineUniform value)
//        {
//            switch (value)
//            {
//                case EEngineUniform.ScreenHeight:
//                case EEngineUniform.ScreenWidth:
//                case EEngineUniform.CameraFovY:
//                case EEngineUniform.CameraFovX:s
//                case EEngineUniform.CameraFarZ:
//                case EEngineUniform.CameraNearZ:
//                case EEngineUniform.CameraAspect:
//                case EEngineUniform.UpdateDelta:
//                    return EShaderVarType._float;

//                case EEngineUniform.ScreenOrigin:
//                    return EShaderVarType._Vector2;

//                case EEngineUniform.CameraPosition:
//                    //case ECommonUniform.CameraForward:
//                    //case ECommonUniform.CameraUp:
//                    //case ECommonUniform.CameraRight:
//                    return EShaderVarType._Vector3;
//            }
//            return EShaderVarType._float;
//        }
//        public string GetDeclaration()
//            => Type.ToString().Substring(1) + _value.ToString();
//        protected override string GetOperation()
//            => _value.ToString();

//        public override void GetDefinition(out string[] inputNames, out string[] outputNames, out MatFuncOverload[] overloads)
//        {
//            throw new System.NotImplementedException();
//        }
//    }
//}
