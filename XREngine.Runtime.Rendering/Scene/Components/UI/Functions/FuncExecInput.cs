//namespace XREngine.Rendering.UI.Functions
//{
//    public interface IFuncExecInput : IBaseFuncExec
//    {
//        void SetConnection(IFuncExecOutput other);
//    }
//    public class FuncExecInput<TOutput, TParent> : BaseFuncExec<TOutput>, IFuncExecInput
//        where TOutput : BaseFuncExec, IFuncExecOutput where TParent : UIComponent, IFunction
//    {
//        public override bool IsOutput => false;
//        public new TParent OwningActor => (TParent)base.OwningActor;

//        public FuncExecInput(string name)
//            : base(name) { }
//        public FuncExecInput(string name, TParent parent)
//            : base(name, parent) { }
        
//        public virtual void SetConnection(IFuncExecOutput other)
//            => SetConnection(other as TOutput);
//        public virtual void SetConnection(TOutput other)
//        {
//            if (other == _connectedTo)
//                return;
//            _connectedTo?.ClearConnection();
//            _connectedTo = other;
//            _connectedTo?.SetConnection(this);
//        }
//        public override bool CanConnectTo(BaseFuncArg other)
//        {
//            if (other != null && other is TOutput input)
//                return true;
//            return false;
//        }
//        public override bool ConnectTo(BaseFuncArg other)
//        {
//            if (!CanConnectTo(other))
//                return false;

//            SetConnection(other as TOutput);
//            return true;
//        }
//    }
//}
