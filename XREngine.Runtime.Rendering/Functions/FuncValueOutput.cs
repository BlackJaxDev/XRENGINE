//namespace XREngine.Rendering.Models.Materials.Functions
//{
//    public class FuncValueOutput<TInput, TParent> : BaseFuncValue<TInput>, IFuncValueOutput
//        where TInput : class, IFuncValueInput where TParent : class, IFunction
//    {
//        public delegate void DelConnected(TInput other);
//        public event DelConnected Connected, Disconnected;

//        public override bool IsOutput => true;
//        public EventList<TInput> Connections => _connections;
//        public new TParent ParentSocket => (TParent)base.ParentSocket;

//        public override bool HasConnection => Connections.Count > 0;

//        IEnumerable<IFuncValueInput> IFuncValueOutput.Connections => Connections;

//        protected EventList<TInput> _connections = new EventList<TInput>(false, false);

//        public FuncValueOutput(string name, TParent parent) : base(name, parent)
//        {
//            _connections.PostAdded += _connectedTo_Added;
//            _connections.PostRemoved += _connectedTo_Removed;
//        }

//        public bool ConnectTo(TInput other)
//        {
//            if (!CanConnectTo(other))
//                return false;
//            _connections.Add(other);
//            return true;
//        }

//        public void CallbackAddConnection(IFuncValueInput other) => CallbackAddConnection(other as TInput);
//        public virtual void CallbackAddConnection(TInput other)
//        {
//            _connections.Add(other, false, false);
//            DetermineBestArgType(other);
//            Connected?.Invoke(other);
//        }
//        public void SecondaryRemoveConnection(IFuncValueInput other) => CallbackRemoveConnection(other as TInput);
//        public virtual void CallbackRemoveConnection(TInput other)
//        {
//            _connections.Remove(other, false, false);
//            DetermineBestArgType(null);
//            Disconnected?.Invoke(other);
//        }
//        public void ClearConnections() => _connections.Clear();

//        private void _connectedTo_Added(TInput item)
//        {
//            item.Connection = this;
//            DetermineBestArgType(item);
//            Connected?.Invoke(item);
//        }
//        private void _connectedTo_Removed(TInput item)
//        {
//            item.ClearConnection();
//            DetermineBestArgType(item);
//            Disconnected?.Invoke(item);
//        }

//        protected virtual void DetermineBestArgType(TInput connection) { }

//        public override bool CanConnectTo(TInput other)
//        {
//            if (other is null || Connections.Contains(other))
//                return false;

//            int otherType = other.CurrentArgumentType;
//            int thisType = CurrentArgumentType;
//            return thisType == otherType;
//        }
//        public override bool CanConnectTo(BaseFuncArg other)
//            => CanConnectTo(other as TInput);
//        public override bool ConnectTo(BaseFuncArg other)
//            => ConnectTo(other as TInput);

//        //IEnumerator<IFuncValueInput> IEnumerable<IFuncValueInput>.GetEnumerator() => _connections.GetEnumerator();
//        public bool ConnectionsContains(IFuncValueInput other) => _connections.Contains(other);
//    }
//}
