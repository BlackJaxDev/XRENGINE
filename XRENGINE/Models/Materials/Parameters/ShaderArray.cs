using System.ComponentModel;

namespace XREngine.Rendering.Models.Materials
{
    public abstract class ShaderArrayBase(string name, IShaderVarOwner? owner) : ShaderVar(name, owner)
    {
        public abstract int Length { get; }
    }
    public class ShaderArray<T> : ShaderArrayBase where T : ShaderVar
    {
        [Browsable(false)]
        public override EShaderVarType TypeName => TypeAssociations[typeof(T)];
        public ShaderArrayValueHandler<T> Value
        {
            get => _value;
            set 
            {
                _value = value;
                OnValueChanged();
            }
        }
        public override int Length => _value.Length;
        protected override void SetProgramUniform(XRRenderProgram program, string location)
        {
            //throw new NotImplementedException();
            //Api.Uniform();
        }
        internal override string GetShaderValueString()
            => _value?.ToString() ?? string.Empty;
        public override object GenericValue => Value;
        
        private ShaderArrayValueHandler<T> _value;

        public ShaderArray(string name)
            : this(name, null) { }
        public ShaderArray(string name, IShaderVarOwner? owner)
            : base(name, owner) { _value = new ShaderArrayValueHandler<T>(); }
        public ShaderArray(ShaderArrayValueHandler<T> defaultValue, string name, IShaderVarOwner? owner)
            : base(name, owner) { _value = defaultValue; }
    }
    public class ShaderArrayValueHandler<T> : IUniformableArray<T> where T : ShaderVar
    {
        public int Length => Values.Length;
        public T[] Values { get; set; }

        public ShaderArrayValueHandler()
        {
            Values = null;
        }
        public ShaderArrayValueHandler(int count)
        {
            Values = new T[count];
        }
    }
}
