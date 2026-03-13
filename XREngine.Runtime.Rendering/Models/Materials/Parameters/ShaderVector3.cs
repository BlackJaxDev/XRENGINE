using Extensions;
using System.ComponentModel;
using System.Numerics;
using XREngine.Core;
using Color = System.Drawing.Color;

namespace XREngine.Rendering.Models.Materials
{
    public class ShaderVector3 : ShaderVar<Vector3>
    {
        [Browsable(false)]
        public override EShaderVarType TypeName => EShaderVarType._vec3;

        protected override void SetProgramUniform(XRRenderProgram program, string location)
            => program.Uniform(location, Value);

        internal override string GetShaderValueString()
             => $"Vector3({_value.X:0.0######}f, {_value.Y:0.0######}f, {_value.Z:0.0######}f)";

        public ShaderVector3()
            : this(new Vector3(), NoName) { }
        public ShaderVector3(Vector3 defaultValue, string name)
            : this(defaultValue, name, null) { }
        public ShaderVector3(Vector3 defaultValue, string name, IShaderVarOwner? owner)
            : base(defaultValue, name, owner)
        {
            _fields.Add(".x", new ShaderFloat(defaultValue.X, "X", this));
            _fields.Add(".y", new ShaderFloat(defaultValue.Y, "Y", this));
            _fields.Add(".z", new ShaderFloat(defaultValue.Z, "Z", this));
        }

        public Color Color
        {
            get => (Color)this;
            set => Value = value.ToVector3();
        }

        public ShaderVector3(float x, float y, float z)
            : this(x, y, z, NoName) { }
        public ShaderVector3(float x, float y, float z, string name)
            : this(x, y, z, name, null) { }
        public ShaderVector3(float x, float y, float z, string name, IShaderVarOwner? owner)
            : this(new Vector3(x, y, z), name, owner) { }

        public static implicit operator ShaderVector3(Color p)
            => new(p.R * Utility.ByteToFloat, p.G * Utility.ByteToFloat, p.B * Utility.ByteToFloat);
        public static explicit operator Color(ShaderVector3 p)
            => Color.FromArgb(p.Value.X.ToByte(), p.Value.Y.ToByte(), p.Value.Z.ToByte());
    }
}
