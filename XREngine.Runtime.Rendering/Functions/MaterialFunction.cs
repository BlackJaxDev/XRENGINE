//using Extensions;

//namespace XREngine.Rendering.Models.Materials.Functions
//{
//    public abstract class MaterialFunction
//        : Function<MatFuncValueInput, MatFuncValueOutput, MatFuncExecInput, MatFuncExecOutput>
//    {
//        public MaterialFunction(bool deferControlArrangement = false) : base(deferControlArrangement) { }

//        public MatFuncOverload[] Overloads { get; private set; }
//        public List<int> CurrentValidOverloads { get; } = new List<int>();
        
//        public abstract void GetDefinition(out string[] inputNames, out string[] outputNames, out MatFuncOverload[] overloads);
//        protected override void CollectArguments()
//        {
//            GetDefinition(out string[] inputNames, out string[] outputNames, out MatFuncOverload[] overloads);

//            Overloads = overloads;
//            ResetValidOverloads();

//            //if (overloads.Where(x => x.Inputs.Length != inputNames.Length || x.Outputs.Length != outputNames.Length).ToArray().Length > 0)
//            //    throw new InvalidOperationException();

//            foreach (string inputName in inputNames)
//                AddValueInput(new MatFuncValueInput(inputName, this));
//            foreach (string outputName in outputNames)
//                AddValueOutput(new MatFuncValueOutput(outputName, this));

//            ArrangeControls();
//        }

//        public static bool CanConnect(MatFuncValueInput input, MatFuncValueOutput output)
//        {
//            return true;
//            //if (input is null || output is null)
//            //    return false;

//            //MaterialFunction inFunc = input.ParentSocket;
//            //MaterialFunction outFunc = output.ParentSocket;
//            //for (int i = 0; i < outFunc.CurrentValidOverloads.Count; ++i)
//            //{
//            //    MatFuncOverload outOverload = outFunc.Overloads[i];
//            //    for (int x = 0; x < inFunc.CurrentValidOverloads.Count; ++x)
//            //    {
//            //        MatFuncOverload inOverload = inFunc.Overloads[x];
//            //        foreach (EGenShaderVarType outGen in outOverload.Outputs)
//            //            foreach (EGenShaderVarType inGen in inOverload.Inputs)
//            //                if ((outGen & inGen) != 0)
//            //                    return true;
//            //    }
//            //}
//            //return false;
//        }

//        internal void RecalcValidOverloads(bool[] validTypes)
//        {
//            CurrentValidOverloads.Clear();
//            int r;
//            for (int i = 0, x = 0; i < validTypes.Length; ++i, ++x)
//                if (!validTypes[i] && (r = CurrentValidOverloads.IndexOf(i)) >= 0)
//                    CurrentValidOverloads.RemoveAt(r);
//        }
//        public void ResetValidOverloads()
//        {
//            CurrentValidOverloads.Clear();
//            for (int i = 0; i < Overloads.Length; ++i)
//                CurrentValidOverloads.Add(i);
//        }
//        public void RecalcValidOverloads()
//        {
//            CurrentValidOverloads.Clear();
//            //foreach (MatFuncValueInput input in _valueInputs)
//            //{
//            //    if (input.HasConnection)
//            //    {
                    
//            //    }
//            //}
//            foreach (MatFuncValueOutput output in _valueOutputs)
//            {
//                if (output.HasConnection)
//                {

//                }
//            }
//            if (CurrentValidOverloads.Count == 1)
//            {
//                MatFuncOverload overload = Overloads[CurrentValidOverloads[0]];
//                for (int i = 0; i < _valueInputs.Count; ++i)
//                {
//                    //_valueInputs[i].CurrentArgumentType = overload.Inputs[i];
//                }
//                foreach (MatFuncValueOutput output in _valueOutputs)
//                {

//                }
//            }
//            else
//            {
//                foreach (MatFuncValueInput input in _valueInputs)
//                {
//                    input.CurrentArgumentType = -1;
//                }
//                foreach (MatFuncValueOutput output in _valueOutputs)
//                {
//                    output.CurrentArgumentType = -1;
//                }
//            }
//        }
        
//        public void CollectInputTreeRecursive(HashSet<MaterialFunction> tree)
//        {
//            if (tree.Add(this))
//                foreach (var input in InputArguments)
//                    if (input.Connection != null)
//                        input.Connection.ParentSocket.CollectInputTreeRecursive(tree);
//        }

//        #region Statics
//        public static string Two(EShaderVarType type)
//        {
//            if (!IsType(type, ShaderVar.BooleanTypes))
//                return type switch
//                {
//                    EShaderVarType._iVector2 => "iVector2(2)",
//                    EShaderVarType._iVector3 => "iVector3(2)",
//                    EShaderVarType._iVector4 => "iVector4(2)",
//                    EShaderVarType._uint => "2",
//                    EShaderVarType._uVector2 => "uVector2(2)",
//                    EShaderVarType._uVector3 => "uVector3(2)",
//                    EShaderVarType._uVector4 => "uVector4(2)",
//                    EShaderVarType._float => "2.0f",
//                    EShaderVarType._Vector2 => "Vector2(2.0f)",
//                    EShaderVarType._Vector3 => "Vector3(2.0f)",
//                    EShaderVarType._Vector4 => "Vector4(2.0f)",
//                    EShaderVarType._double => "2.0",
//                    EShaderVarType._dVector2 => "dVector2(2.0)",
//                    EShaderVarType._dVector3 => "dVector3(2.0)",
//                    EShaderVarType._dVector4 => "dVector4(2.0)",
//                    EShaderVarType._mat3 => throw new NotImplementedException(),
//                    EShaderVarType._mat4 => throw new NotImplementedException(),
//                    _ => "2",
//                };
//            throw new ArgumentException();
//        }
//        public static string One(EShaderVarType type)
//        {
//            return type switch
//            {
//                EShaderVarType._bool => "true",
//                EShaderVarType._bVector2 => "bVector2(true)",
//                EShaderVarType._bVector3 => "bVector3(true)",
//                EShaderVarType._bVector4 => "bVector4(true)",
//                EShaderVarType._iVector2 => "iVector2(1)",
//                EShaderVarType._iVector3 => "iVector3(1)",
//                EShaderVarType._iVector4 => "iVector4(1)",
//                EShaderVarType._uint => "1",
//                EShaderVarType._uVector2 => "uVector2(1)",
//                EShaderVarType._uVector3 => "uVector3(1)",
//                EShaderVarType._uVector4 => "uVector4(1)",
//                EShaderVarType._float => "1.0f",
//                EShaderVarType._Vector2 => "Vector2(1.0f)",
//                EShaderVarType._Vector3 => "Vector3(1.0f)",
//                EShaderVarType._Vector4 => "Vector4(1.0f)",
//                EShaderVarType._double => "1.0",
//                EShaderVarType._dVector2 => "dVector2(1.0)",
//                EShaderVarType._dVector3 => "dVector3(1.0)",
//                EShaderVarType._dVector4 => "dVector4(1.0)",
//                EShaderVarType._mat3 => throw new NotImplementedException(),
//                EShaderVarType._mat4 => throw new NotImplementedException(),
//                _ => "1",
//            };

//            //throw new ArgumentException();
//        }
//        public static string Zero(EShaderVarType type)
//        {
//            return type switch
//            {
//                EShaderVarType._bool => "false",
//                EShaderVarType._bVector2 => "bVector2(false)",
//                EShaderVarType._bVector3 => "bVector3(false)",
//                EShaderVarType._bVector4 => "bVector4(false)",
//                EShaderVarType._iVector2 => "iVector2(0)",
//                EShaderVarType._iVector3 => "iVector3(0)",
//                EShaderVarType._iVector4 => "iVector4(0)",
//                EShaderVarType._uint => "0",
//                EShaderVarType._uVector2 => "uVector2(0)",
//                EShaderVarType._uVector3 => "uVector3(0)",
//                EShaderVarType._uVector4 => "uVector4(0)",
//                EShaderVarType._float => "0.0f",
//                EShaderVarType._Vector2 => "Vector2(0.0f)",
//                EShaderVarType._Vector3 => "Vector3(0.0f)",
//                EShaderVarType._Vector4 => "Vector4(0.0f)",
//                EShaderVarType._double => "0.0",
//                EShaderVarType._dVector2 => "dVector2(0.0)",
//                EShaderVarType._dVector3 => "dVector3(0.0)",
//                EShaderVarType._dVector4 => "dVector4(0.0)",
//                EShaderVarType._mat3 => throw new NotImplementedException(),
//                EShaderVarType._mat4 => throw new NotImplementedException(),
//                _ => "0",
//            };
//            throw new ArgumentException();
//        }
//        public static string Half(EShaderVarType type)
//        {
//            if (IsType(type, ShaderVar.DecimalTypes))
//                return type switch
//                {
//                    EShaderVarType._float => "0.5f",
//                    EShaderVarType._Vector2 => "Vector2(0.5f)",
//                    EShaderVarType._Vector3 => "Vector3(0.5f)",
//                    EShaderVarType._Vector4 => "Vector4(0.5f)",
//                    EShaderVarType._dVector2 => "dVector2(0.5)",
//                    EShaderVarType._dVector3 => "dVector3(0.5)",
//                    EShaderVarType._dVector4 => "dVector4(0.5)",
//                    EShaderVarType._mat3 => throw new NotImplementedException(),
//                    EShaderVarType._mat4 => throw new NotImplementedException(),
//                    _ => "0.5",
//                };
//            throw new ArgumentException();
//        }

//        public static bool IsType(EShaderVarType type, EShaderVarType[] comparedTypes)
//            => comparedTypes.Contains(type);

//        #endregion
//    }
//}
