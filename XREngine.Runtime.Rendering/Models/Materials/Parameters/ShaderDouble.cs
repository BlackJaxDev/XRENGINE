using System.ComponentModel;

namespace XREngine.Rendering.Models.Materials
{
    public class ShaderDouble(double defaultValue, string name, IShaderVarOwner? owner) : ShaderVar(name, owner),
        IUniformable,
        IShaderDoubleType, 
        IShaderNonVectorType,
        IShaderNumericType,
        IShaderDecimalType, 
        IShaderSignedType
    {
        [Browsable(false)]
        public override EShaderVarType TypeName => EShaderVarType._double;
        [Category(CategoryName)]
        public double Value { get => defaultValue; set { defaultValue = value; OnValueChanged(); } }
        protected override void SetProgramUniform(XRRenderProgram program, string location)
            => program.Uniform(location, defaultValue);
        [Browsable(false)]
        public unsafe double* Data { get { fixed (double* ptr = &defaultValue) return ptr; } }
        internal override string GetShaderValueString() => defaultValue.ToString("0.0######");
        [Browsable(false)]
        public override object GenericValue => Value;

        public ShaderDouble() : this(0.0, NoName) { }
        public ShaderDouble(double defaultValue, string name)
            : this(defaultValue, name, null) { }
    }
}
