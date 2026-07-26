//using XREngine.Rendering.UI.Functions;

//namespace XREngine.Rendering.Models.Materials.Functions
//{
//    [FunctionDefinition(
//        "Logic",
//        "For Loop",
//        "Runs code X amount of times.",
//        "loop for foreach repeat repetition")]
//    public class ForLoopFunc : ShaderLogic
//    {
//        public ForLoopFunc() : base() { }

//        public override void GetDefinition(out string[] inputNames, out string[] outputNames, out MatFuncOverload[] overloads)
//        {
//            throw new System.NotImplementedException();
//        }

//        public override string GetLogicFormat()
//        {
//            return @"for (int )";
//        }

//        //protected override string GetOperation() => "for (int i = {0}; {1}, ";
        
//        protected override MatFuncValueInput[] GetValueInputs()
//        {
//            return new MatFuncValueInput[]
//            {
//                //new MatFuncValueInput("Start Index", EShaderVarType._int),
//                //new MatFuncValueInput("Loop Operation", EShaderVarType._bool),
//                //TODO: material function argument for each loop?
//            };
//        }
//        protected override MatFuncValueOutput[] GetValueOutputs()
//        {
//            return new MatFuncValueOutput[]
//            {
//                //new MatFuncValueOutput("Loop Index", EShaderVarType._int),
//                //new MatFuncValueOutput("Loop Count", EShaderVarType._bool),
//                //TODO: material function argument for each loop?
//            };
//        }
//    }
//}
