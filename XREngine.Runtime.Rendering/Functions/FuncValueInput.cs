//namespace XREngine.Rendering.Models.Materials.Functions
//{
//    public class FuncValueInput<TOutput, TParent> : BaseFuncValue<TOutput>, IFuncValueInput
//        where TOutput : UIComponent, IFuncValueOutput where TParent : UIComponent, IFunction
//    {
//        public delegate void DelConnected(TOutput other);
//        public event DelConnected Connected, Disconnected;

//        public override bool IsOutput => false;
//        public new TParent ParentSocket => (TParent)base.ParentSocket;
//        public TOutput Connection
//        {
//            get => _connection;
//            set => ConnectTo(value);
//        }
//        IFuncValueOutput IFuncValueInput.Connection
//        {
//            get => Connection;
//            set => ConnectTo(value as TOutput);
//        }
//        public override bool HasConnection => Connection != null;

//        protected TOutput _connection;

//        public FuncValueInput(string name, TParent parent) : base(name, parent) { }

//        public bool ConnectTo(TOutput other)
//        {
//            if (!CanConnectTo(other))
//                return false;
//            SetConnection(other);
//            return true;
//        }
//        protected virtual void SetConnection(TOutput other)
//        {
//            if (_connection != null)
//            {
//                _connection.SecondaryRemoveConnection(this);
//                Disconnected?.Invoke(_connection);
//            }
//            _connection = other;
//            if (_connection != null)
//            {
//                _connection.CallbackAddConnection(this);
//                DetermineBestArgType(_connection);
//                Connected?.Invoke(_connection);
//            }
//            else
//                DetermineBestArgType(null);
//        }

//        protected virtual void DetermineBestArgType(TOutput connection) { }

//        public virtual void ClearConnection()
//        {
//            if (_connection != null)
//            {
//                _connection.SecondaryRemoveConnection(this);
//                DetermineBestArgType(null);
//                Disconnected?.Invoke(_connection);
//            }
//            _connection = null;
//        }

//        protected override void OnCurrentArgTypeChanged()
//        {
//            _connection.CurrentArgumentType = CurrentArgumentType;
//        }
//        public override bool CanConnectTo(TOutput other)
//        {
//            if (other is null || Connection == other)
//                return false;

//            int otherType = other.CurrentArgumentType;
//            int thisType = CurrentArgumentType;
//            return thisType == otherType;
//        }
//        public override bool CanConnectTo(BaseFuncArg other)
//            => CanConnectTo(other as TOutput);
//        public override bool ConnectTo(BaseFuncArg other)
//            => ConnectTo(other as TOutput);
//    }
//}
