using System.ComponentModel;
using System.Numerics;

namespace XREngine.Rendering.Models.Materials.Shaders.Parameters
{
    public class ShaderMat4 : ShaderVar<Matrix4x4>
    {
        [Browsable(false)]
        public override EShaderVarType TypeName => EShaderVarType._mat4;

        protected override void SetProgramUniform(XRRenderProgram program, string location)
            => program.Uniform(location, Value);

        public ShaderMat4()
            : this(Matrix4x4.Identity, NoName) { }
        public ShaderMat4(Matrix4x4 defaultValue, string name)
            : this(defaultValue, name, null) { }
        public ShaderMat4(Matrix4x4 defaultValue, string name, IShaderVarOwner? owner)
            : base(defaultValue, name, owner)
        {
            _canSwizzle = false;
            //_fields.Add("[0]", new ShaderVector4(defaultValue.Row0, "Row0", this));
            //_fields.Add("[1]", new ShaderVector4(defaultValue.Row1, "Row1", this));
            //_fields.Add("[2]", new ShaderVector4(defaultValue.Row2, "Row2", this));
            //_fields.Add("[3]", new ShaderVector4(defaultValue.Row3, "Row3", this));
        }
    }
    //public class ShaderMat3 : ShaderVar
    //{
    //    [Browsable(false)]
    //    public override EShaderVarType TypeName => EShaderVarType._mat3;
    //    [Category(CategoryName)]
    //    public Matrix3 Value { get => _value; set { _value = value; OnValueChanged(); } }
    //    internal override void SetProgramUniform(RenderProgram program, int location) 
    //        => program.Uniform(location, _value);
    //    internal override string GetShaderValueString() => _value.ToString();
    //    [Browsable(false)]
    //    public override object GenericValue => Value;

    //    [TSerialize(ValueName, IsElementString = true)]
    //    private Matrix3 _value;

    //    public ShaderMat3() : this(Matrix3.Identity, NoName) { }
    //    public ShaderMat3(Matrix3 defaultValue, string name)
    //        : this(defaultValue, name, null) { }
    //    public ShaderMat3(Matrix3 defaultValue, string name, IShaderVarOwner owner)
    //        : base(name, owner)
    //    {
    //        _canSwizzle = false;
    //        _value = defaultValue;
    //        _fields.Add("[0]", new ShaderVector3(defaultValue.Row0, "Row0", this));
    //        _fields.Add("[1]", new ShaderVector3(defaultValue.Row1, "Row1", this));
    //        _fields.Add("[2]", new ShaderVector3(defaultValue.Row2, "Row2", this));
    //    }
    //}
}
