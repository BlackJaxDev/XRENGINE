//using System.Numerics;

//namespace XREngine.Rendering.Models.Materials.Functions
//{
//    public class MatFuncValueInput : FuncValueInput<MatFuncValueOutput, MaterialFunction>
//    {
//        public ShaderVar? DefaultValue { get; private set; } = null;
//        public EShaderVarType ArgumentType
//        {
//            get => (EShaderVarType)CurrentArgumentType;
//            set => CurrentArgumentType = (int)value;
//        }

//        public EGenShaderVarType[] GetPossibleValidTypes()
//        {
//            EGenShaderVarType[] types = new EGenShaderVarType[ParentSocket.CurrentValidOverloads.Count];
//            for (int x = 0; x < types.Length; ++x)
//                types[x] = ParentSocket.Overloads[ParentSocket.CurrentValidOverloads[x]].Inputs[ArgumentIndex];
//            return types.ToArray();
//        }

//        protected override void OnCurrentArgTypeChanged()
//        {
//            DefaultValue = ArgumentType == EShaderVarType._invalid
//                ? null
//                : Activator.CreateInstance(ShaderVar.ShaderTypeAssociations[ArgumentType]) as ShaderVar;
//        }

//        public override Vector4 GetTypeColor()
//            => ShaderVar.GetTypeColor(ArgumentType);

//        protected override void DetermineBestArgType(MatFuncValueOutput connection)
//        {
//            if (connection is null)
//            {

//            }
//            else
//            {
//                //EGenShaderVarType[]
//                //    possibleInTypes = GetPossibleValidTypes(),
//                //    possibleOutTypes = connection.GetPossibleTypes();
//                bool[]
//                    validInTypes = new bool[ParentSocket.Overloads.Length],
//                    validOutTypes = new bool[connection.ParentSocket.Overloads.Length];
//                //for (int i = 0; i < validInTypes.Length; ++i)
//                //{
//                //    EGenShaderVarType t = ParentSocket.Overloads[i].Inputs[ArgumentIndex];
//                //    validInTypes[i] = 
//                //        ParentSocket.CurrentValidOverloads.Contains(i) &&
//                //        possibleOutTypes.Any(x => (x & possibleInTypes[i]) != 0);
//                //}
//                //for (int i = 0; i < validOutTypes.Length; ++i)
//                //{
//                //    EGenShaderVarType t = connection.ParentSocket.Overloads[i].Outputs[ArgumentIndex];
//                //    validOutTypes[i] = 
//                //        connection.ParentSocket.CurrentValidOverloads.Contains(i) && 
//                //        possibleInTypes.Any(x => (x & possibleOutTypes[i]) != 0);
//                //}
//                //connection.ParentSocket.RecalcValidOverloads(validOutTypes);
//                //ParentSocket.RecalcValidOverloads(validInTypes);
//            }
//        }

//        public override bool CanConnectTo(MatFuncValueOutput other)
//            => MaterialFunction.CanConnect(this, other);

//        public MatFuncValueInput(string name, MaterialFunction parent) : base(name, parent) { }
//    }
//}
