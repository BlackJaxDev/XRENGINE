//using Extensions;
//using System.Collections.Concurrent;
//using System.ComponentModel;
//using XREngine.Data.Core;
//using XREngine.Data.Vectors;
//using XREngine.Input;

//namespace XREngine.Rendering
//{
//    /// <summary>
//    /// A render handler is what handles processing a visuals
//    /// for a renderer to display on the UI through the render context.
//    /// </summary>
//    public abstract class BaseRenderHandler : XRBase, IEnumerable<XRViewport>
//    {
//        public virtual int MaxViewports => 4;
//        public ERenderLibrary RenderLibrary { get; set; } = ERenderLibrary.Vulkan;

//        /// <summary>
//        /// The RenderContext for this handler 
//        /// is what handles tying a renderer to the UI.
//        /// </summary>
//        public virtual WindowContext? Context
//        {
//            get => _context;
//            set => _context = value;
//        }
//        private WindowContext? _context;

//        public int Width { get; private set; }
//        public int Height { get; private set; }

//        public abstract void Render();
//        public abstract void PreRenderUpdate();
//        public abstract void SwapBuffers();

//        public virtual void GotFocus() { }
//        public virtual void LostFocus() { }
//        public virtual void MouseEnter() { }
//        public virtual void MouseLeave() { }

//        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
//        [Browsable(false)]
//        [EditorBrowsable(EditorBrowsableState.Never)]
//        public List<ELocalPlayerIndex> ValidPlayerIndices { get; set; } =
//        [
//            ELocalPlayerIndex.One,
//            ELocalPlayerIndex.Two,
//            ELocalPlayerIndex.Three,
//            ELocalPlayerIndex.Four,
//        ];

//        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
//        [Browsable(false)]
//        [EditorBrowsable(EditorBrowsableState.Never)]
//        public ConcurrentDictionary<ELocalPlayerIndex, XRViewport> Viewports { get; private set; } = new ConcurrentDictionary<ELocalPlayerIndex, XRViewport>();

//        public WindowManager WorldManager
//        {
//            get => _worldManager;
//            internal set
//            {
//                OnWorldManagerPreChanged();
//                _worldManager = value;
//                OnWorldManagerPostChanged();
//            }
//        }

//        IReadOnlyDictionary<ELocalPlayerIndex, XRViewport> IRenderHandler.Viewports => Viewports;

//        public event Action WorldManagerPreChanged;
//        public event Action WorldManagerPostChanged;
//        protected virtual void OnWorldManagerPreChanged() => WorldManagerPreChanged?.Invoke();
//        protected virtual void OnWorldManagerPostChanged() => WorldManagerPostChanged?.Invoke();

//        public IEnumerator<XRViewport> GetEnumerator() => ((IEnumerable<XRViewport>)Viewports).GetEnumerator();
//        IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<XRViewport>)Viewports).GetEnumerator();

//        public virtual void Resize(int width, int height)
//        {
//            Width = width;
//            Height = height;
//            foreach (XRViewport v in Viewports.Values)
//                v.Resize(Width, Height, true);
//        }

//        public virtual void Closed() { }
//    }
//    /// <summary>
//    /// This is what handles tying a renderer to the UI.
//    /// </summary>
//    public abstract partial class WindowContext : XRBase, IDisposable
//    {
//        public static WindowContext? Hovered { get; set; }
//        public static WindowContext? Focused { get; set; }

//        public enum EPanelType
//        {
//            Hovered,
//            Focused,
//            Rendering,
//        }

//        public delegate void ContextChangedEventHandler(bool isCurrent);
//        public event ContextChangedEventHandler? ContextChanged;
//        //public event EventHandler ResetOccured;

//        public abstract AbstractRenderer Renderer { get; }

//        private long _resizeWidthHeight = 0L;

//        private BaseRenderHandler _renderHandler;
//        public BaseRenderHandler Handler
//        {
//            get => _renderHandler;
//            set
//            {
//                if (_renderHandler != null && _renderHandler.Context == this)
//                    _renderHandler.Context = null;
//                _renderHandler = value;
//                if (_renderHandler != null)
//                    _renderHandler.Context = this;
//            }
//        }

//        public static List<WindowContext> BoundContexts { get; } = [];

//        private static WindowContext? _captured;
//        public static WindowContext? Captured
//        {
//            get => _captured;
//            set
//            {
//                if (_captured == value)
//                {
//                    if (_captured != null && _captured.IsCurrent())
//                        _captured.SetCurrent(true);
//                    return;
//                }

//                if (value is null && _captured != null && _captured.IsCurrent())
//                    _captured.SetCurrent(false);

//                _captured = value;

//                if (_captured != null)
//                {
//                    _captured.SetCurrent(true);
//                    _captured.ContextChanged?.Invoke(false);
//                }
//            }
//        }

//        public List<GenericRenderObject.ContextBind> States { get; private set; } = [];

//        private EVSyncMode _vsyncMode = EVSyncMode.Adaptive;
//        public EVSyncMode VSyncMode
//        {
//            get => _vsyncMode;
//            set
//            {
//                _vsyncMode = value;
//                foreach (ThreadSubContext c in _subContexts.Values)
//                    c.VsyncChanged(_vsyncMode);
//            }
//        }

//        public IntPtr? Handle => _handle;

//        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
//        [Browsable(false)]
//        [EditorBrowsable(EditorBrowsableState.Never)]
//        public Point ScreenLocation { get; set; }

//        public Point PointToClient(Point p)
//        {
//            p.X -= ScreenLocation.X;
//            p.Y -= ScreenLocation.Y;
//            p.Y = Handler.Height - p.Y;
//            return p;
//        }
//        public Point PointToScreen(Point p)
//        {
//            p.Y = Handler.Height - p.Y;
//            p.X += ScreenLocation.X;
//            p.Y += ScreenLocation.Y;
//            return p;
//        }

//        protected IntPtr? _handle;
//        protected bool _resetting = false;

//        protected ConcurrentDictionary<int, ThreadSubContext> _subContexts = new();
//        protected ThreadSubContext? _currentSubContext;

//        public WindowContext() : this(null) { }
//        public WindowContext(IntPtr? handle)
//        {
//            _handle = handle;
//            //if (_handle != null)
//            //    _handle.Resize += OnResized;
//            BoundContexts.Add(this);
//        }

//        private void OnResized(object sender, EventArgs e)
//        {
//            //OnResized();
//            //_control.Invalidate();
//        }

//        protected void GetCurrentSubContext(bool allowContextCreation)
//        {
//            int id = Environment.CurrentManagedThreadId;

//            if (_subContexts.ContainsKey(id) || (allowContextCreation && CreateContextForThread() >= 0))
//                (_currentSubContext = _subContexts[id])?.SetCurrent(true);
//        }
//        protected internal abstract ThreadSubContext CreateSubContext(IntPtr? handle, Thread thread);
//        internal int CreateContextForThread()
//        {
//            Thread thread = Thread.CurrentThread;

//            if (thread is null)
//                return -1;

//            if (!_subContexts.ContainsKey(thread.ManagedThreadId))
//            {
//                ThreadSubContext c = CreateSubContext(_handle, thread);
//                if (c != null)
//                {
//                    c.OnResized(IVector2.Zero);
//                    c.Generate();
//                    _subContexts.TryAdd(thread.ManagedThreadId, c);
//                }
//                else
//                {
//                    Debug.WriteLine("Failed to generate render subcontext.");
//                    return -1;
//                }
//            }

//            return thread.ManagedThreadId;
//        }
//        internal void DestroyContextForThread(Thread thread)
//        {
//            if (thread is null)
//                return;

//            int id = thread.ManagedThreadId;
//            if (_subContexts.ContainsKey(id))
//            {
//                _subContexts.TryRemove(id, out ThreadSubContext? value);
//                value?.Dispose();
//            }
//        }
//        public void CollectVisible() => Handler.PreRenderUpdate();
//        public void SwapBuffers() => Handler.SwapBuffers();
//        public void Render()
//        {
//            Capture();
//            GetCurrentSubContext(true);
//            CheckSize();
//            BeforeRender();
//            Handler.Render();
//            AfterRender();
//            Swap();
//            ErrorCheck();
//        }

//        private void CheckSize()
//        {
//            long value = Interlocked.Read(ref _resizeWidthHeight);
//            if (value == 0L)
//                return;

//            int width = (int)((value >> 32) & 0xFFFFFFFF);
//            int height = (int)(value & 0xFFFFFFFF);
//            Interlocked.Exchange(ref _resizeWidthHeight, 0L);

//            _currentSubContext?.OnResized(new IVector2(width, height));

//            Handler?.Resize(width, height);
//        }

//        public bool IsCurrent()
//        {
//            GetCurrentSubContext(false);
//            return _currentSubContext?.IsCurrent() ?? false;
//        }
//        public bool IsContextDisposed()
//        {
//            GetCurrentSubContext(false);
//            return _currentSubContext?.IsContextDisposed() ?? true;
//        }
//        protected void OnSwapBuffers()
//        {
//            GetCurrentSubContext(true);
//            try
//            {
//                if (!IsContextDisposed())
//                    _currentSubContext?.OnSwapBuffers();
//            }
//            catch (Exception ex)
//            {
//                Debug.WriteLine(ex);
//            }
//        }
//        public void SetCurrent(bool current)
//        {
//            GetCurrentSubContext(false);
//            _currentSubContext?.SetCurrent(current);
//        }

//        public abstract void ErrorCheck();
//        public void Capture(bool force = false)
//        {
//            //try
//            //{
//            if (force || Captured != this)
//            {
//                if (force)
//                    Captured = null;
//                Captured = this;
//                DestroyQueued();
//                if (!IsInitialized)
//                    Initialize();
//            }
//            //}
//            //catch { Reset(); }
//        }

//        private void DestroyQueued()
//        {
//            while (DeletionQueue.TryDequeue(out GenericRenderObject? obj))
//                obj?.Delete();
//        }

//        public void Release()
//        {
//            //try
//            //{
//            if (Captured == this)
//            {
//                Captured = null;
//                ContextChanged?.Invoke(false);
//            }
//            //}
//            //catch { Reset(); }
//        }

//        private ConcurrentQueue<GenericRenderObject> DeletionQueue { get; } = new ConcurrentQueue<GenericRenderObject>();
//        public void QueueDelete(GenericRenderObject obj) => DeletionQueue.Enqueue(obj);

//        public void Swap()
//        {
//            Capture();
//            OnSwapBuffers();
//        }

//        internal abstract void BeforeRender();
//        internal abstract void AfterRender();

//        //public void Reset()
//        //{
//        //    if (_resetting) //Prevent a possible infinite loop
//        //        return;

//        //    _resetting = true;
//        //    //_control.Reset();
//        //    Dispose();

//        //    //_winInfo = Utilities.CreateWindowsWindowInfo(_control.Handle);
//        //    //_context = new GraphicsContext(GraphicsMode.Default, WindowInfo);
//        //    Capture(true);
//        //    //_context.LoadAll();
//        //    Update();

//        //    ResetOccured?.Invoke(this, EventArgs.Empty);

//        //    _resetting = false;
//        //}
//        //public void Update()
//        //{
//        //    //if (Captured == this)
//        //    //    OnResized();
//        //}
//        public bool IsInitialized { get; protected set; }
//        public abstract void Flush();
//        public abstract void Initialize();
//        public abstract void BeginDraw();
//        public abstract void EndDraw();
//        //public virtual void Unbind() { }

//        public void Resize(int width, int height)
//        {
//            long newValue = ((long)width << 32) | (long)height;
//            Interlocked.Exchange(ref _resizeWidthHeight, newValue);
//        }

//        public virtual void LostFocus() => Handler?.LostFocus();
//        public virtual void GotFocus() => Handler?.GotFocus();
//        public virtual void MouseLeave() => Handler?.MouseLeave();
//        public virtual void MouseEnter() => Handler?.MouseEnter();

//        #region IDisposable Support
//        protected bool _disposedValue = false; // To detect redundant calls
//        public event Action Disposing;
//        protected virtual void Dispose(bool disposing)
//        {
//            if (!_disposedValue)
//            {
//                if (disposing)
//                {
//                    IsInitialized = true;
//                    Capture();
//                    //Unbind();

//                    foreach (GenericRenderObject.ContextBind state in States)
//                        state.Destroy();

//                    States.Clear();
//                    States = null;

//                    BoundContexts.Remove(this);

//                    Release();
//                    //_handle.Resize -= OnResized;
//                    //_handle = null;
//                }
//                Disposing?.Invoke();
//            }
//        }

//        public void RecreateSelf()
//        {
//            if (Handle is null)
//            {
//                //TODO
//            }
//            else
//            {
//                //TODO
//                //(Control.FromHandle(Handle.Value) as IRenderPanel)?.CreateContext();
//            }
//        }

//        public void QueueDisposeSelf()
//        {
//            //TODO
//            //Engine.DisposingRenderContexts.Enqueue(this);
//        }

//        // This code added to correctly implement the disposable pattern.
//        public void Dispose()
//        {
//            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
//            Dispose(true);
//            GC.SuppressFinalize(this);
//        }

//        public void Added(WindowManager worldManager)
//        {

//        }

//        public void Removed(WindowManager worldManager)
//        {

//        }

//        #endregion
//    }
//}
