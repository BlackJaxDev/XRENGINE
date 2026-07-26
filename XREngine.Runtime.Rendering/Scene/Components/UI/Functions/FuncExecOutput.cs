//namespace XREngine.Rendering.UI.Functions
//{
//    public interface IFuncExecOutput : IBaseFuncExec
//    {
//        void SetConnection(IFuncExecInput other);
//    }
//    public class FuncExecOutput<TInput, TParent> : BaseFuncExec<TInput>, IFuncExecOutput
//        where TInput : BaseFuncExec, IFuncExecInput where TParent : UIComponent, IFunction
//    {
//        public override bool IsOutput => true;
//        public new TParent OwningActor => (TParent)base.OwningActor;

//        public FuncExecOutput(string name)
//            : base(name) { }
//        public FuncExecOutput(string name, TParent parent)
//            : base(name, parent) { }
        
//        public virtual void SetConnection(IFuncExecInput other)
//            => SetConnection(other as TInput);
//        public virtual void SetConnection(TInput other)
//        {
//            if (other == _connectedTo)
//                return;
//            _connectedTo?.ClearConnection();
//            _connectedTo = other;
//            _connectedTo?.SetConnection(this);
//        }
//        public override bool CanConnectTo(BaseFuncArg other)
//        {
//            if (other != null && other is TInput input)
//                return true;
//            return false;
//        }
//        public override bool ConnectTo(BaseFuncArg other)
//        {
//            if (!CanConnectTo(other))
//                return false;

//            SetConnection(other as TInput);
//            return true;
//        }
//    }
//}
