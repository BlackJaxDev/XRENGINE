using Extensions;
using System.ComponentModel;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Vectors;
using XREngine.Rendering.Models.Materials.Shaders.Parameters;
using Color = System.Drawing.Color;

namespace XREngine.Rendering.Models.Materials
{
    public interface IUniformableArray : IUniformable { }
    //public interface IUniformable { }
    public interface IShaderVarOwner : IUniformable { }
    public interface IShaderVarType { }

    public interface IShaderNumericType : IShaderVarType { }
    public interface IShaderBooleanType : IShaderVarType { }
    public interface IShaderMatrixType : IShaderVarType { }
    public interface IShaderSignedIntType : IShaderVarType { }
    public interface IShaderUnsignedIntType : IShaderVarType { }
    public interface IShaderFloatType : IShaderVarType { }
    public interface IShaderDoubleType : IShaderVarType { }

    public interface IShaderNonVectorType : IShaderVarType { }
    public interface IShaderVectorType : IShaderVarType { }
    
    public interface IShaderVector2Type : IShaderVarType { }
    public interface IShaderVector3Type : IShaderVarType { }
    public interface IShaderVector4Type : IShaderVarType { }

    public interface IShaderVectorBoolType : IShaderVarType { }
    public interface IShaderVectorSignedIntType : IShaderVarType { }
    public interface IShaderVectorUnsignedIntType : IShaderVarType { }
    public interface IShaderVectorFloatType : IShaderVarType { }
    public interface IShaderVectorDoubleType : IShaderVarType { }
    
    public interface IShaderNonDecimalType : IShaderVarType { }
    public interface IShaderDecimalType : IShaderVarType { }

    public interface IShaderSignedType : IShaderVarType { }
    public interface IShaderUnsignedType : IShaderVarType { }

    public abstract class ShaderVar<T> : ShaderVar where T : struct
    {
        protected T _value;
        public T Value
        {
            get => _value;
            set
            {
                SetField(ref _value, value);
                OnValueChanged();
            }
        }

        public void SetValue(T value)
            => Value = value;

        internal override string GetShaderValueString()
            => Value.ToString() ?? string.Empty;

        [Browsable(false)]
        public override object GenericValue => Value;

        public ShaderVar()
            : this(default, NoName) { }
        public ShaderVar(T defaultValue, string name)
            : this(defaultValue, name, null) { }
        public ShaderVar(T defaultValue, string name, IShaderVarOwner? owner)
            : base(name, owner) => _value = defaultValue;
        public ShaderVar(string name, IShaderVarOwner? owner)
            : base(name, owner) { }
    }
    public abstract class ShaderVar : XRBase, IShaderVarOwner, IUniformable
    {
        internal const string CategoryName = "Material Parameter";
        internal const string ValueName = "Value";
        public const string NoName = "NoName";

        public event Action<ShaderVar>? ValueChanged;

        //Determines if this var's components can be moved around
        protected bool _canSwizzle = true;
        protected Dictionary<string, ShaderVar> _fields = [];

        protected IShaderVarOwner? _owner;
        internal IShaderVarOwner? Owner => _owner;

        public abstract EShaderVarType TypeName { get; }

        private string _name = string.Empty;
        [Browsable(true)]
        [Category(CategoryName)]
        [DisplayName("Uniform Name")]
        public string Name
        {
            get => _name;
            set => SetField(ref _name, (value ?? "").ReplaceWhitespace(""));
        }

        [Browsable(false)]
        public abstract object GenericValue { get; }

        protected bool _valueChanged = true;
        public void SetUniform(XRRenderProgram program, string? nameOverride = null, bool forceUpdate = false)
        {
            if (!_valueChanged && !forceUpdate)
                return;
            
            SetProgramUniform(program, nameOverride ?? Name);
            _valueChanged = false;
        }

        //internal void SetUniform(string name) { SetUniform(Api.GetUniformLocation(programBindingId, name)); }
        //internal void SetUniform() { SetUniform(Api.GetUniformLocation(programBindingId, Name)); }

        protected abstract void SetProgramUniform(XRRenderProgram program, string name);

        public ShaderVar(string userName, IShaderVarOwner? owner)
        {
            _owner = owner;
            Name = userName;
        }

        protected void OnValueChanged()
        {
            _valueChanged = true;
            ValueChanged?.Invoke(this);
        }

        /// <summary>
        /// Ex: layout (location = 0) uniform float potato;
        /// </summary>
        internal string GetUniformDeclaration(int bindingLocation = -1)
        {
            string line = "";
            if (bindingLocation >= 0)
                line = string.Format("layout (location = {0}) ", bindingLocation);
            return line + string.Format("uniform {0};", GetDeclaration());
        }

        internal string GetDeclaration()
            => string.Format("{0} {1}", TypeName.ToString()[1..], Name);

        internal abstract string GetShaderValueString();
        /// <summary>
        /// Ex: this is float '.x', parent is Vector4 '[0]', parent is mat4 'tomato': tomato[0].x
        /// </summary>
        /// <returns></returns>
        internal virtual string AccessorTree()
        {
            return Name;
        }

        internal static Vector4 GetTypeColor(EShaderVarType argumentType)
            => argumentType switch
            {
                EShaderVarType._bool or EShaderVarType._bvec2 or EShaderVarType._bvec3 or EShaderVarType._bvec4 => (ColorF4)Color.Red,
                EShaderVarType._int or EShaderVarType._ivec2 or EShaderVarType._ivec3 or EShaderVarType._ivec4 => (ColorF4)Color.HotPink,
                EShaderVarType._uint or EShaderVarType._uvec2 or EShaderVarType._uvec3 or EShaderVarType._uvec4 => (ColorF4)Color.Orange,
                EShaderVarType._float or EShaderVarType._vec2 or EShaderVarType._vec3 or EShaderVarType._vec4 => (ColorF4)Color.Blue,
                EShaderVarType._double or EShaderVarType._dvec2 or EShaderVarType._dvec3 or EShaderVarType._dvec4 => (ColorF4)Color.Green,
                _ => (ColorF4)Color.Black,
            };

        #region Type caches
        //public static EShaderVarType[] GetTypesMatching<T>() where T : IShaderVarType
        //{
        //    Type varType = typeof(T);
        //    Type shaderType = typeof(ShaderVar);
        //    var types = AppDomainHelper.FindTypes(t => t.IsSubclassOf(shaderType) && varType.IsAssignableFrom(t));
        //    return types.Select(x => TypeAssociations[x]).Distinct().ToArray();
        //}
        public static readonly Dictionary<Type, EShaderVarType> TypeAssociations = new()
        {
            { typeof(ShaderBool),   EShaderVarType._bool   },
            { typeof(ShaderInt),    EShaderVarType._int    },
            { typeof(ShaderUInt),   EShaderVarType._uint   },
            { typeof(ShaderFloat),  EShaderVarType._float  },
            { typeof(ShaderDouble), EShaderVarType._double },
            { typeof(ShaderVector2),   EShaderVarType._vec2   },
            { typeof(ShaderVector3),   EShaderVarType._vec3   },
            { typeof(ShaderVector4),   EShaderVarType._vec4   },
            //{ typeof(ShaderMat3),   EShaderVarType._mat3   },
            { typeof(ShaderMat4),   EShaderVarType._mat4   },
            { typeof(ShaderIVector2),  EShaderVarType._ivec2  },
            { typeof(ShaderIVector3),  EShaderVarType._ivec3  },
            { typeof(ShaderIVector4),  EShaderVarType._ivec4  },
            { typeof(ShaderUVector2),  EShaderVarType._uvec2  },
            { typeof(ShaderUVector3),  EShaderVarType._uvec3  },
            { typeof(ShaderUVector4),  EShaderVarType._uvec4  },
            { typeof(ShaderDVector2),  EShaderVarType._dvec2  },
            { typeof(ShaderDVector3),  EShaderVarType._dvec3  },
            { typeof(ShaderDVector4),  EShaderVarType._dvec4  },
            { typeof(ShaderBVector2),  EShaderVarType._bvec2  },
            { typeof(ShaderBVector3),  EShaderVarType._bvec3  },
            { typeof(ShaderBVector4),  EShaderVarType._bvec4  },
        };
        public static readonly Dictionary<EShaderVarType, Type> ShaderTypeAssociations = new()
        {
            { EShaderVarType._bool,     typeof(ShaderBool)      },
            { EShaderVarType._int,      typeof(ShaderInt)       },
            { EShaderVarType._uint,     typeof(ShaderUInt)      },
            { EShaderVarType._float,    typeof(ShaderFloat)     },
            { EShaderVarType._double,   typeof(ShaderDouble)    },
            { EShaderVarType._vec2,     typeof(ShaderVector2)      },
            { EShaderVarType._vec3,  typeof(ShaderVector3)   },
            { EShaderVarType._vec4,     typeof(ShaderVector4)      },
            //{ EShaderVarType._mat3,     typeof(ShaderMat3)      },
            { EShaderVarType._mat4,     typeof(ShaderMat4)      },
            { EShaderVarType._ivec2,    typeof(ShaderIVector2)     },
            { EShaderVarType._ivec3, typeof(ShaderIVector3)  },
            { EShaderVarType._ivec4,    typeof(ShaderIVector4)     },
            { EShaderVarType._uvec2,    typeof(ShaderUVector2)     },
            { EShaderVarType._uvec3, typeof(ShaderUVector3)  },
            { EShaderVarType._uvec4,    typeof(ShaderUVector4)     },
            { EShaderVarType._dvec2,    typeof(ShaderDVector2)     },
            { EShaderVarType._dvec3, typeof(ShaderDVector3)  },
            { EShaderVarType._dvec4,    typeof(ShaderDVector4)     },
            { EShaderVarType._bvec2,    typeof(ShaderBVector2)     },
            { EShaderVarType._bvec3, typeof(ShaderBVector3)  },
            { EShaderVarType._bvec4,    typeof(ShaderBVector4)     },
        };
        public static readonly Dictionary<EShaderVarType, Type> AssemblyTypeAssociations = new()
        {
            { EShaderVarType._bool,     typeof(bool)        },
            { EShaderVarType._int,      typeof(int)         },
            { EShaderVarType._uint,     typeof(uint)        },
            { EShaderVarType._float,    typeof(float)       },
            { EShaderVarType._double,   typeof(double)      },
            { EShaderVarType._vec2,     typeof(Vector2)     },
            { EShaderVarType._vec3,     typeof(Vector3)     },
            { EShaderVarType._vec4,     typeof(Vector4)     },
            //{ EShaderVarType._mat3,     typeof(Matrix3)     },
            { EShaderVarType._mat4,     typeof(Matrix4x4)   },
            { EShaderVarType._ivec2,    typeof(IVector2)    },
            { EShaderVarType._ivec3,    typeof(IVector3)    },
            { EShaderVarType._ivec4,    typeof(IVector4)    },
            { EShaderVarType._uvec2,    typeof(UVector2)    },
            { EShaderVarType._uvec3,    typeof(UVector3)    },
            { EShaderVarType._uvec4,    typeof(UVector4)    },
            { EShaderVarType._dvec2,    typeof(DVector2)    },
            { EShaderVarType._dvec3,    typeof(DVector3)    },
            { EShaderVarType._dvec4,    typeof(DVector4)    },
            { EShaderVarType._bvec2,    typeof(BoolVector2) },
            { EShaderVarType._bvec3,    typeof(BoolVector3) },
            { EShaderVarType._bvec4,    typeof(BoolVector4) },
        };

        /// <summary>
        /// Creates a new <see cref="ShaderVar"/> instance with a default value for the given GLSL type.
        /// Returns <c>null</c> for unsupported types (e.g. samplers, mat3).
        /// </summary>
        public static ShaderVar? CreateForType(EShaderVarType type, string name) => type switch
        {
            EShaderVarType._bool   => new ShaderBool(false, name),
            EShaderVarType._int    => new ShaderInt(0, name),
            EShaderVarType._uint   => new ShaderUInt(0u, name),
            EShaderVarType._float  => new ShaderFloat(0f, name),
            EShaderVarType._double => new ShaderDouble(0.0, name),
            EShaderVarType._vec2   => new ShaderVector2(default, name),
            EShaderVarType._vec3   => new ShaderVector3(default, name),
            EShaderVarType._vec4   => new ShaderVector4(default, name),
            EShaderVarType._mat4   => new ShaderMat4(Matrix4x4.Identity, name),
            EShaderVarType._ivec2  => new ShaderIVector2(default, name),
            EShaderVarType._ivec3  => new ShaderIVector3(default, name),
            EShaderVarType._ivec4  => new ShaderIVector4(default, name),
            EShaderVarType._uvec2  => new ShaderUVector2(default, name),
            EShaderVarType._uvec3  => new ShaderUVector3(default, name),
            EShaderVarType._uvec4  => new ShaderUVector4(default, name),
            EShaderVarType._dvec2  => new ShaderDVector2(default, name),
            EShaderVarType._dvec3  => new ShaderDVector3(default, name),
            EShaderVarType._dvec4  => new ShaderDVector4(default, name),
            EShaderVarType._bvec2  => new ShaderBVector2(default, name),
            EShaderVarType._bvec3  => new ShaderBVector3(default, name),
            EShaderVarType._bvec4  => new ShaderBVector4(default, name),
            _ => null,
        };

        /// <summary>
        /// Maps GLSL type names to their corresponding <see cref="EShaderVarType"/>.
        /// Only non-opaque, non-sampler types are included.
        /// </summary>
        public static readonly Dictionary<string, EShaderVarType> GlslTypeMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["bool"]   = EShaderVarType._bool,
            ["bvec2"]  = EShaderVarType._bvec2,
            ["bvec3"]  = EShaderVarType._bvec3,
            ["bvec4"]  = EShaderVarType._bvec4,
            ["int"]    = EShaderVarType._int,
            ["ivec2"]  = EShaderVarType._ivec2,
            ["ivec3"]  = EShaderVarType._ivec3,
            ["ivec4"]  = EShaderVarType._ivec4,
            ["uint"]   = EShaderVarType._uint,
            ["uvec2"]  = EShaderVarType._uvec2,
            ["uvec3"]  = EShaderVarType._uvec3,
            ["uvec4"]  = EShaderVarType._uvec4,
            ["float"]  = EShaderVarType._float,
            ["vec2"]   = EShaderVarType._vec2,
            ["vec3"]   = EShaderVarType._vec3,
            ["vec4"]   = EShaderVarType._vec4,
            ["double"] = EShaderVarType._double,
            ["dvec2"]  = EShaderVarType._dvec2,
            ["dvec3"]  = EShaderVarType._dvec3,
            ["dvec4"]  = EShaderVarType._dvec4,
            ["mat3"]   = EShaderVarType._mat3,
            ["mat4"]   = EShaderVarType._mat4,
        };

        public static readonly EShaderVarType[] SignedIntTypes =
        [
            EShaderVarType._int,
            EShaderVarType._ivec2,
            EShaderVarType._ivec3,
            EShaderVarType._ivec4,
        ];
        public static readonly EShaderVarType[] UnsignedIntTypes =
        [
            EShaderVarType._uint,
            EShaderVarType._uvec2,
            EShaderVarType._uvec3,
            EShaderVarType._uvec4,
        ];
        public static readonly EShaderVarType[] IntegerTypes =
        [
            EShaderVarType._int,
            EShaderVarType._uint,
            EShaderVarType._ivec2,
            EShaderVarType._uvec2,
            EShaderVarType._ivec3,
            EShaderVarType._uvec3,
            EShaderVarType._ivec4,
            EShaderVarType._uvec4,
        ];
        public static readonly EShaderVarType[] DecimalTypes =
        [
            EShaderVarType._float,
            EShaderVarType._double,
            EShaderVarType._vec2,
            EShaderVarType._dvec2,
            EShaderVarType._vec3,
            EShaderVarType._dvec3,
            EShaderVarType._vec4,
            EShaderVarType._dvec4,
        ];
        public static readonly EShaderVarType[] FloatTypes =
        [
            EShaderVarType._float,
            EShaderVarType._vec2,
            EShaderVarType._vec3,
            EShaderVarType._vec4,
        ];
        public static readonly EShaderVarType[] DoubleTypes =
        [
            EShaderVarType._double,
            EShaderVarType._dvec2,
            EShaderVarType._dvec3,
            EShaderVarType._dvec4,
        ];
        public static readonly EShaderVarType[] NumericTypes =
        [
            EShaderVarType._float,
            EShaderVarType._double,
            EShaderVarType._int,
            EShaderVarType._uint,
            EShaderVarType._vec2,
            EShaderVarType._ivec2,
            EShaderVarType._uvec2,
            EShaderVarType._dvec2,
            EShaderVarType._vec3,
            EShaderVarType._ivec3,
            EShaderVarType._uvec3,
            EShaderVarType._dvec3,
            EShaderVarType._vec4,
            EShaderVarType._ivec4,
            EShaderVarType._uvec4,
            EShaderVarType._dvec4,
        ];
        public static readonly EShaderVarType[] SignedTypes =
        [
            EShaderVarType._float,
            EShaderVarType._double,
            EShaderVarType._int,
            EShaderVarType._vec2,
            EShaderVarType._ivec2,
            EShaderVarType._dvec2,
            EShaderVarType._vec3,
            EShaderVarType._ivec3,
            EShaderVarType._dvec3,
            EShaderVarType._vec4,
            EShaderVarType._ivec4,
            EShaderVarType._dvec4,
        ];
        public static readonly EShaderVarType[] BooleanTypes =
        [
            EShaderVarType._bool,
            EShaderVarType._bvec2,
            EShaderVarType._bvec3,
            EShaderVarType._bvec4,
        ];
        public static readonly EShaderVarType[] VectorTypes =
        [
            EShaderVarType._vec2,
            EShaderVarType._ivec2,
            EShaderVarType._uvec2,
            EShaderVarType._dvec2,
            EShaderVarType._bvec2,
            EShaderVarType._vec3,
            EShaderVarType._ivec3,
            EShaderVarType._uvec3,
            EShaderVarType._dvec3,
            EShaderVarType._bvec3,
            EShaderVarType._vec4,
            EShaderVarType._ivec4,
            EShaderVarType._uvec4,
            EShaderVarType._dvec4,
            EShaderVarType._bvec4,
        ];
        #endregion
    }

    /// <summary>
    /// Literal GLSL type names with a _ appended to the front.
    /// Must match the type names in GLSL.
    /// </summary>
    public enum EShaderVarType
    {
        _bool,
        _int,
        _uint,
        _float,
        _double,
        _vec2,
        _vec3,
        _vec4,
        _mat3,
        _mat4,
        _ivec2,
        _ivec3,
        _ivec4,
        _uvec2,
        _uvec3,
        _uvec4,
        _dvec2,
        _dvec3,
        _dvec4,
        _bvec2,
        _bvec3,
        _bvec4,
        _sampler2D
    }
}
