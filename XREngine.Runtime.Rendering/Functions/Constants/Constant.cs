//using XREngine.Rendering.UI.Functions;

//namespace XREngine.Rendering.Models.Materials.Functions
//{
//    [FunctionDefinition(
//        "Constants",
//        "Constant Value",
//        "Hardcodes a constant value in the shader.",
//        "constant scalar vector parameter value")]
//    public class ConstantFunc<T> : BaseConstantFunc where T : ShaderVar, new()
//    {
//        public ConstantFunc() : this(default) { }
//        public ConstantFunc(T value) : base() => Value = value;
        
//        private T _value;
//        public T Value
//        {
//            get => _value;
//            set
//            {
//                _value = value ?? new T();
//                _value.ValueChanged += _value_ValueChanged;
//                _headerString.Text = _value.GenericValue.ToString();
//                Overloads[0].Outputs[0] = (EGenShaderVarType)ShaderVar.TypeAssociations[typeof(T)];
//                ArrangeControls();
//            }
//        }

//        private void _value_ValueChanged(ShaderVar obj)
//        {
//            _headerString.Text = obj.GenericValue.ToString();
//            ArrangeControls();
//        }

//        protected override string GetOperation() => _value.GetShaderValueString();

//        public override ShaderVar GetVar() => Value;

//        public override void GetDefinition(out string[] inputNames, out string[] outputNames, out MatFuncOverload[] overloads)
//        {
//            throw new System.NotImplementedException();
//        }
//    }
//    public abstract class BaseConstantFunc : ShaderMethod
//    {
//        public BaseConstantFunc() : base() { }

//        public abstract ShaderVar GetVar();
//    }
//}
