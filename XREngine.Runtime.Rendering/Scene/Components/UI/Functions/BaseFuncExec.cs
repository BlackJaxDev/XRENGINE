//using XREngine.Rendering.Models.Materials;

//namespace XREngine.Rendering.UI.Functions
//{
//    public interface IBaseFuncExec : IUIComponent
//    {
//        void ClearConnection();
//    }
//    public abstract class BaseFuncExec : BaseFuncArg
//    {
//        public static ColorF4 DefaultColor { get; } = new ColorF4(0.7f, 1.0f);

//        public abstract BaseFuncExec ConnectedToGeneric { get; }

//        public BaseFuncExec(string name) : base(name, DefaultColor) { }
//        public BaseFuncExec(string name, IFunction parent) : base(name, parent, DefaultColor) { }
//    }
//    public abstract class BaseFuncExec<TInput> : BaseFuncExec where TInput : BaseFuncExec, IBaseFuncExec
//    {
//        public TInput ConnectedTo => _connectedTo;
//        public override BaseFuncExec ConnectedToGeneric => ConnectedTo;

//        protected TInput _connectedTo;

//        public BaseFuncExec(string name) : base(name) { }
//        public BaseFuncExec(string name, IFunction parent) : base(name, parent) { }
        
//        public virtual void ClearConnection()
//        {
//            TInput temp = _connectedTo;
//            _connectedTo = null;
//            temp?.ClearConnection();
//        }
//    }
//}
