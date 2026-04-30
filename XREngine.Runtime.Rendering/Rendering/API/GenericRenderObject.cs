namespace XREngine.Rendering
{
    //public interface IGenericRenderObject
    //{
    //    bool IsActive { get; }
    //    int BindingId { get; }
    //    EObjectType Type { get; }
    //    int Generate();
    //    void Delete();
    //    void Destroy();
    //}
    //public partial class GenericRenderObject : XRBase
    //{
    //    public const int NullBindingId = 0;
    //    //public AbstractRenderer Renderer { get; set; }

    //    internal ContextBind? CurrentBind
    //    {
    //        get
    //        {
    //            //Make sure current bind is up to date
    //            GetCurrentBind();
    //            return _currentBind;
    //        }
    //    }

    //    //public bool IsActive => CurrentBind?.Active ?? false;

    //    //[Browsable(false)]
    //    //public int BindingId
    //    //{
    //    //    get
    //    //    {
    //    //        GetCurrentBind();

    //    //        //Generate if not active already
    //    //        if (!IsActive)
    //    //            Generate();

    //    //        return CurrentBind?.BindingId ?? 0;
    //    //    }
    //    //}

    //    /// <summary>
    //    /// List of all render contexts this object has been generated on.
    //    /// </summary>
    //    private readonly List<ContextBind> _owners = [];

    //    /// <summary>
    //    /// The last context that this object has been bound to or called the binding id from.
    //    /// </summary>
    //    private ContextBind? _currentBind = null;

    //    public event Action? Generated;

    //    //public GenericRenderObject(EObjectType type) => Type = type;
    //    //public GenericRenderObject(EObjectType type, int bindingId)
    //    //{
    //    //    Type = type;
    //    //    CurrentBind.BindingId = bindingId;
    //    //    PostGenerated();
    //    //}

    //    private void DestroyContextBind(ContextBind bind)
    //    {
    //        if (_currentBind != null && _currentBind == bind)
    //            _currentBind = null;

    //        _owners.Remove(bind);

    //        //Delete();
    //    }

    //    protected bool GetCurrentBind()
    //    {
    //        if (WindowContext.Captured is null)
    //        {
    //            if (_currentBind is null || _currentBind.Context != null)
    //                _currentBind = new ContextBind(null, this);
    //            //throw new Exception("No context bound.");
    //            return false;
    //        }
    //        else if (_currentBind is null || _currentBind.Context != WindowContext.Captured)
    //        {
    //            //This part is very important; switches contexts based on captured context for different render panels
    //            int index = _owners.FindIndex(x => x.Context == WindowContext.Captured);
    //            if (index >= 0)
    //                _currentBind = _owners[index];
    //            else
    //                _owners.Add(_currentBind = new ContextBind(WindowContext.Captured, this));
    //        }
    //        return true;
    //    }
    //    /// <summary>
    //    /// Performs all checks needed and creates this render object on the current render context if need be.
    //    /// Call after capturing a context.
    //    /// </summary>
    //    //public int Generate()
    //    //{
    //    //    //Make sure current bind is up to date
    //    //    bool hasBind = GetCurrentBind();
    //    //    if (!hasBind)
    //    //    {
    //    //        //Engine.LogWarning("Unable to create render object: no captured render context.");
    //    //        return NullBindingId;
    //    //    }

    //    //    if (IsActive)
    //    //        return BindingId;

    //    //    PreGenerated();

    //    //    int id = CreateObject();
    //    //    if (id != 0)
    //    //    {
    //    //        var bind = CurrentBind;
    //    //        if (bind != null)
    //    //        {
    //    //            bind.BindingId = id;
    //    //            //bind.GenerationStackTrace = Engine.GetStackTrace();
    //    //            bind.GenerationTime = DateTime.Now;
    //    //            bind.ThreadID = Environment.CurrentManagedThreadId;
    //    //            PostGenerated();
    //    //            Generated?.Invoke();
    //    //        }
    //    //    }
    //    //    //else
    //    //    //    Engine.LogWarning("Unable to create render object.");

    //    //    return id;
    //    //}

    //    /// <summary>
    //    /// Do not call. Override if special generation necessary.
    //    /// </summary>
    //    /// <returns>The handle to the object.</returns>
    //    //protected virtual int CreateObject()
    //    //    => Renderer.CreateObject(Type);

    //    //protected virtual void PreGenerated() { }
    //    ///// <summary>
    //    ///// Called directly after this object is created on the current context.
    //    ///// </summary>
    //    //protected virtual void PostGenerated() { }
    //    ///// <summary>
    //    ///// Called directly before this object is deleted from the current context.
    //    ///// </summary>
    //    //protected virtual void PreDeleted() { }
    //    ///// <summary>
    //    ///// Called directly after this object is deleted from the current context.
    //    ///// </summary>
    //    //protected virtual void PostDeleted() { }
    //    ///// <summary>
    //    ///// Deletes this object from ALL contexts.
    //    ///// </summary>
    //    //public virtual void Destroy()
    //    //{
    //    //    ContextBind bind;
    //    //    while (_owners.Count > 0)
    //    //    {
    //    //        bind = _owners[0];

    //    //        if (bind.Context != null && !bind.Context.IsContextDisposed())
    //    //            bind.Context.QueueDelete(this);

    //    //        _owners.Remove(bind);
    //    //    }
    //    //}
    //    /// <summary>
    //    /// Removes this render object from the current context (RenderPanel).
    //    /// Call after capturing a context.
    //    /// </summary>
    //    //internal protected void Delete()
    //    //{
    //    //    if (WindowContext.Captured is null)
    //    //        return;

    //    //    //Remove current bind from owners list
    //    //    if (_currentBind is null || _currentBind.Context != WindowContext.Captured)
    //    //    {
    //    //        //Get current bind manually so it isn't created if it never existed in the first place
    //    //        int index = _owners.FindIndex(x => x.Context == WindowContext.Captured);
    //    //        if (index >= 0)
    //    //        {
    //    //            _currentBind = _owners[index];
    //    //            _owners.RemoveAt(index);
    //    //        }
    //    //        else
    //    //            return; //This state was never generated on this context in the first place
    //    //    }
    //    //    else
    //    //    {
    //    //        _owners.Remove(_currentBind);
    //    //    }

    //    //    if (!IsActive)
    //    //        return;

    //    //    PreDeleted();

    //    //    Renderer.DeleteObject(Type, _currentBind._bindingId);

    //    //    _currentBind._bindingId = 0;
    //    //    _currentBind.Context = null;
    //    //    _currentBind.GenerationStackTrace = null;
    //    //    _currentBind.GenerationTime = null;
    //    //    _currentBind.Destroy();
    //    //    PostDeleted();
    //    //}

    //    //public override int GetHashCode() => ToString().GetHashCode();
    //    //public override string ToString() => $"{Type} [{CurrentBind?.BindingId ?? -1}]";
    //    //public override bool Equals(object obj) => obj != null && ToString().Equals(obj.ToString());

    //    //#region IDisposable Support
    //    //protected bool _disposedValue = false; // To detect redundant calls

    //    //protected virtual void Dispose(bool disposing)
    //    //{
    //    //    if (!_disposedValue)
    //    //    {
    //    //        if (disposing)
    //    //        {
    //    //            Destroy();
    //    //        }

    //    //        _disposedValue = true;
    //    //    }
    //    //}

    //    //// This code added to correctly implement the disposable pattern.
    //    //public virtual void Dispose()
    //    //{
    //    //    // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
    //    //    Dispose(true);
    //    //}
    //    //#endregion
    //}
}
