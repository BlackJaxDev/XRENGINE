//using System.ComponentModel;

//namespace XREngine.Rendering.Models.Materials.Functions
//{
//    [Flags]
//    public enum EShaderStageFlag
//    {
//        None        = 0b000000,
//        Fragment    = 0b000001,
//        Vertex      = 0b000010,
//        Geometry    = 0b000100,
//        TessEval    = 0b001000,
//        TessCtrl    = 0b010000,
//        Compute     = 0b100000,
//        All         = 0b111111,
//    }
//    public abstract class ShaderMethod : MaterialFunction
//    {
//        public const string OperatorCategoryName = "Operators";
//        public const string ComparisonCategoryName = "Comparisons";
//        public const string TrigCategoryName = "Trigonometry";
//        public const string ModifierCategoryName = "Modifiers";

//        private bool _returnInline;

//        /// <summary>
//        /// If true, writes within the given scope.
//        /// If false, writes a method before main.
//        /// </summary>
//        [Browsable(false)]
//        public bool Inline { get; protected set; } = true;
//        /// <summary>
//        /// If true, writes a variable declaration before main using GetGlobalVarDec().
//        /// </summary>
//        [Browsable(false)]
//        public bool HasGlobalVarDec { get; protected set; } = false;
//        /// <summary>
//        /// If true, can be written within a single line (do not include a semicolon in GetOperation()).
//        /// If false, uses its own space. Use as many lines as necessary and include semicolons.
//        /// </summary>
//        [Browsable(false)]
//        public bool ReturnsInline
//        {
//            get => Inline || OutputArguments.Count == 1 || _returnInline;
//            set => _returnInline = value;
//        }
//        public EShaderStageFlag UsableIn { get; } = EShaderStageFlag.All;

//        public ShaderMethod() : this(true, EShaderStageFlag.All) { }
//        public ShaderMethod(bool inline, EShaderStageFlag usableIn = EShaderStageFlag.All) : base()
//        {
//            Inline = inline;
//            UsableIn = usableIn;
//        }
//        //public ShaderMethod(params EShaderVarType[] outputTypes) : base(true)
//        //{
//        //    AddValueOutput(new MatFuncValueOutput(string.Empty, outputTypes));
//        //    Inline = true;
//        //    AddArguments();
//        //}
        
//        public List<MeshParam> NecessaryMeshParams { get; } = new List<MeshParam>();
//        public List<EEngineUniform> NecessaryEngineParams { get; } = new List<EEngineUniform>();

//        /// <summary>
//        /// Returns the base operation for string.Format.
//        /// </summary>
//        protected abstract string GetOperation();
//        public virtual string GetGlobalVarDec()
//        {
//            if (!Inline)
//                return GetMethodSyntax();
//            return string.Empty;
//        }
        
//        public string GetCodeSyntax(out bool returnsInline)
//        {
//            returnsInline = ReturnsInline;
//            if (ReturnsInline)
//                return GetLineSyntax();
//            else
//            {
//                string[] outputNames = GetOutputNames();
//                string[] inputNames = GetInputNames();
//                if (outputNames.Length > 0)
//                {
//                    if (inputNames.Length > 0)
//                        return string.Format(GetOperation(), inputNames, outputNames);
//                    else
//                        return string.Format(GetOperation(), GetOutputNames());
//                }
//                else
//                {
//                    if (inputNames.Length > 0)
//                        return string.Format(GetOperation(), inputNames);
//                    else
//                        return string.Format(GetOperation());
//                }
//            }
//        }

//        public virtual string GetLineSyntax()
//        {
//            if (!ReturnsInline)
//                throw new InvalidOperationException();

//            string[] inputNames = GetInputNames();
//            string[] outputNames = GetOutputNames();

//            if (Inline)
//                return string.Format(GetOperation(), inputNames);
            
//            //Write method call syntax
//            string s = FunctionName + "(";
//            if (_valueInputs.Count > 0)
//            {
//                s += inputNames[0];
//                for (int i = 1; i < _valueInputs.Count; ++i)
//                    s += ", " + inputNames[i];
//            }
//            if (_valueOutputs.Count > 0)
//            {
//                s += outputNames[0];
//                for (int i = 1; i < _valueOutputs.Count; ++i)
//                    s += ", " + outputNames[i];
//            }
//            s += ")";

//            return s;
//        }

//        public string[] GetOutputNames()
//            => OutputArguments.Select(x => x.OutputVarName).ToArray();
//        public string[] GetInputNames()
//            => InputArguments.Select(x => x.Connection is null ? x.DefaultValue?.GetShaderValueString() ?? "" : x.Connection.OutputVarName).ToArray();
        
//        public string GetMethodSyntax()
//        {
//            string type;

//            if (OutputArguments.Count == 1)
//                type = OutputArguments[0].ArgumentType.ToString().Substring(1);
//            else
//                type = "void";

//            string s = type + " " + FunctionName + "(";
//            bool first = true;

//            foreach (MatFuncValueInput arg in InputArguments)
//            {
//                if (first)
//                    first = false;
//                else
//                    s += ", ";
//                s += "in " + arg.ArgumentType.ToString().Substring(1) + " " + arg.Name;
//            }

//            if (OutputArguments.Count > 1)
//                foreach (MatFuncValueOutput arg in OutputArguments)
//                {
//                    if (first)
//                        first = false;
//                    else
//                        s += ", ";
//                    s += "out " + arg.ArgumentType.ToString().Substring(1) + " " + arg.Name;
//                }

//            s += ")" + 
//                Environment.NewLine + "{" + Environment.NewLine + 
//                GetOperation() +
//                Environment.NewLine + "}" + Environment.NewLine;

//            return s;
//        }
//    }
//}
