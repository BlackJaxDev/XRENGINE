namespace XREngine.Rendering
{
    //public partial class GenericRenderObject
    //{
    //    public class ContextBind : XRBase
    //    {
    //        //internal bool _generatedFailSafe = false;
    //        internal int _bindingId = NullBindingId;

    //        internal ContextBind(WindowContext context, GenericRenderObject parentState)
    //        {
    //            ParentState = parentState;
    //            Context = context;
    //            context?.States.Add(this);
    //        }

    //        public int ThreadID { get; set; }
    //        public DateTime? GenerationTime { get; internal set; } = null;
    //        public string? GenerationStackTrace { get; internal set; }

    //        public bool Active => BindingId > NullBindingId/* || _generatedFailSafe*/;
    //        public int BindingId
    //        {
    //            get => _bindingId;
    //            set
    //            {
    //                //if (_bindingId > NullBindingId)
    //                //    throw new Exception("Context binding already has an id!");
    //                _bindingId = value;
    //                //if (_bindingId == 0)
    //                //    _generatedFailSafe = true;
    //            }
    //        }

    //        public GenericRenderObject ParentState { get; }
    //        internal WindowContext Context { get; set; } = null;

    //        public void Destroy() => ParentState.DestroyContextBind(this);
    //        public override string ToString() => ParentState.ToString();
    //    }
    //}

    //public delegate void DelContextsChanged(WindowContext context, bool added);
}
