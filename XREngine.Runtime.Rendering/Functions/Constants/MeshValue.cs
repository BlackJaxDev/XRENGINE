//using Extensions;
//using XREngine.Rendering.Shaders.Generator;

//namespace XREngine.Rendering.Models.Materials.Functions
//{
//    public class MeshParam
//    {
//        public event Action? Changed;

//        public MeshParam()
//        {
//            Value = EMeshValue.FragPos;
//            Index = 0;
//        }
//        public MeshParam(EMeshValue value, uint index)
//        {
//            Value = value;
//            Index = index;
//        }

//        private EMeshValue _value;
//        private uint _index;

//        private void Update()
//        {
//            switch (_value)
//            {
//                default:
//                case EMeshValue.FragPos:
//                    Type = EShaderVarType._Vector3;
//                    ShaderBaseLocation = DefaultVertexShaderGenerator.FragPosBaseLoc;
//                    MaxCount = 1;
//                    Index = Index.ClampMax(MaxCount - 1);
//                    ShaderLocation = ShaderBaseLocation + Index;
//                    break;
//                case EMeshValue.FragNorm:
//                    Type = EShaderVarType._Vector3;
//                    ShaderBaseLocation = DefaultVertexShaderGenerator.FragNormBaseLoc;
//                    MaxCount = 1;
//                    Index = Index.ClampMax(MaxCount - 1);
//                    ShaderLocation = ShaderBaseLocation + Index;
//                    break;
//                case EMeshValue.FragBinorm:
//                    Type = EShaderVarType._Vector3;
//                    ShaderBaseLocation = DefaultVertexShaderGenerator.FragBinormBaseLoc;
//                    MaxCount = 1;
//                    Index = Index.ClampMax(MaxCount - 1);
//                    ShaderLocation = ShaderBaseLocation + Index;
//                    break;
//                case EMeshValue.FragTan:
//                    Type = EShaderVarType._Vector3;
//                    ShaderBaseLocation = DefaultVertexShaderGenerator.FragTanBaseLoc;
//                    MaxCount = 1;
//                    Index = Index.ClampMax(MaxCount - 1);
//                    ShaderLocation = ShaderBaseLocation + Index;
//                    break;
//                case EMeshValue.FragUV:
//                    Type = EShaderVarType._Vector2;
//                    ShaderBaseLocation = DefaultVertexShaderGenerator.FragUVBaseLoc;
//                    MaxCount = XRMeshDescriptor.MaxTexCoords;
//                    Index = Index.ClampMax(MaxCount - 1);
//                    ShaderLocation = ShaderBaseLocation + Index;
//                    break;
//                case EMeshValue.FragColor:
//                    Type = EShaderVarType._Vector4;
//                    ShaderBaseLocation = DefaultVertexShaderGenerator.FragColorBaseLoc;
//                    MaxCount = XRMeshDescriptor.MaxColors;
//                    Index = Index.ClampMax(MaxCount - 1);
//                    ShaderLocation = ShaderBaseLocation + Index;
//                    break;
//            }
//            Changed?.Invoke();
//        }

//        public uint Index
//        {
//            get => _index;
//            set
//            {
//                uint temp = value.Clamp(0, MaxCount - 1);
//                if (_index != temp)
//                {
//                    _index = temp;
//                    Update();
//                }
//            }
//        }
//        public EMeshValue Value
//        {
//            get => _value;
//            set
//            {
//                _value = value;
//                Update();
//            }
//        }

//        public uint MaxCount { get; private set; }
//        public EShaderVarType Type { get; private set; }
//        public uint ShaderBaseLocation { get; private set; }
//        public uint ShaderLocation { get; private set; }

//        public string GetVariableName()
//            => Value.ToString() + (MaxCount > 1 ? Index.ToString() : "");

//        public string GetVariableInDeclaration()
//            => "layout(location = " + ShaderLocation + ") in " + Type.ToString().Substring(1) + " " + GetVariableName() + ";";

//        public override int GetHashCode()
//            => (int)ShaderLocation;
//        public override bool Equals(object? obj)
//            => obj is MeshParam p && p.ShaderLocation == ShaderLocation;
//    }
//    [FunctionDefinition(
//        "Constants",
//        "Mesh Value",
//        "Provides a value from the mesh to the shader.",
//        "mesh value")]
//    public class MeshValueFunc : ShaderMethod
//    {
//        public MeshValueFunc() : this(EMeshValue.FragPos) { }
//        public MeshValueFunc(EMeshValue value) : base()
//        {
//            Param.Changed += Param_Changed;
//            Param.Value = value;
//            NecessaryMeshParams.Add(Param);
//        }

//        private void Param_Changed()
//        {
//            //OutputArguments[0].AllowedArgumentTypes = new int[] { (int)Param.Type };
//            Overloads[0].Outputs[0] = (EGenShaderVarType)Param.Type;
//            _headerString.Text = Param.GetVariableName();
//            ArrangeControls();
//        }

//        public MeshParam Param { get; } = new MeshParam();

//        public static EShaderVarType GetType(EMeshValue value)
//        {
//            switch (value)
//            {
//                default:
//                case EMeshValue.FragPos:
//                case EMeshValue.FragNorm:
//                case EMeshValue.FragBinorm:
//                case EMeshValue.FragTan:
//                    return EShaderVarType._Vector3;
//                case EMeshValue.FragUV:
//                    return EShaderVarType._Vector2;
//                case EMeshValue.FragColor:
//                    return EShaderVarType._Vector4;
//            }
//        }

//        //public override string GetGlobalVarDec() => Param.GetVariableInDeclaration();
//        protected override string GetOperation() => Param.GetVariableName();

//        public override void GetDefinition(out string[] inputNames, out string[] outputNames, out MatFuncOverload[] overloads)
//        {
//            inputNames = [];
//            outputNames =
//            [
//                string.Empty,
//            ];
//            overloads =
//            [
//                new(EGLSLVersion.Ver_110, EGenShaderVarType.Vector3, false),
//            ];
//        }
//    }
//}
