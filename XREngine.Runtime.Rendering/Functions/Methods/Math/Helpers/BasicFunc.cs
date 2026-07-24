//namespace XREngine.Rendering.Models.Materials.Functions
//{
//    public abstract class BasicCustomOverloadFunc : ShaderMethod
//    {
//        public BasicCustomOverloadFunc() : base(true) { }
//        protected virtual string GetInputName() => string.Empty;
//        protected virtual string GetOutputName() => string.Empty;
//        protected override string GetOperation() => GetFuncName() + "({0})";
//        protected abstract string GetFuncName();
//        protected abstract MatFuncOverload[] GetInOutOverloads();
//        public override void GetDefinition(out string[] inputNames, out string[] outputNames, out MatFuncOverload[] overloads)
//        {
//            inputNames = new string[] { GetInputName() };
//            outputNames = new string[] { GetOutputName() };
//            overloads = GetInOutOverloads();
//        }
//    }
//    public abstract class BasicSingleOverloadFunc : BasicCustomOverloadFunc
//    {
//        public BasicSingleOverloadFunc() : base() { }
//        protected abstract EGenShaderVarType GetGenInOutType();
//        protected abstract EGLSLVersion GetVersion();
//        protected override MatFuncOverload[] GetInOutOverloads()
//            => new MatFuncOverload[] { new MatFuncOverload(GetVersion(), GetGenInOutType()), };
//    }
//    public abstract class BasicGenFloatFunc : BasicSingleOverloadFunc
//    {
//        public BasicGenFloatFunc() : base() { }
//        protected override EGenShaderVarType GetGenInOutType() => EGenShaderVarType.GenFloat;
//    }
//    public abstract class BasicGenDoubleFunc : BasicSingleOverloadFunc
//    {
//        public BasicGenDoubleFunc() : base() { }
//        protected override EGenShaderVarType GetGenInOutType() => EGenShaderVarType.GenDouble;
//    }
//    public abstract class BasicGenIntFunc : BasicSingleOverloadFunc
//    {
//        public BasicGenIntFunc() : base() { }
//        protected override EGenShaderVarType GetGenInOutType() => EGenShaderVarType.GenInt;
//    }
//    public abstract class BasicGenUIntFunc : BasicSingleOverloadFunc
//    {
//        public BasicGenUIntFunc() : base() { }
//        protected override EGenShaderVarType GetGenInOutType() => EGenShaderVarType.GenUInt;
//    }
//    public abstract class BasicGenBoolFunc : BasicSingleOverloadFunc
//    {
//        public BasicGenBoolFunc() : base() { }
//        protected override EGenShaderVarType GetGenInOutType() => EGenShaderVarType.GenBool;
//    }
//}
