using Extensions;
using System.Buffers;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Reflection;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.UI;
using YamlDotNet.Serialization;

namespace XREngine.Scene.Transforms
{
    /// <summary>
    /// Represents the basis for transforming a scene node in the hierarchy.
    /// Inherit from this class to create custom transformation implementations, or use the Transform class for default functionality.
    /// This class is thread-safe.
    /// </summary>
    [Serializable]
    public abstract partial class TransformBase : XRWorldObjectBase, IRenderable
    {
        public RenderInfo[] RenderedObjects { get; }
        [YamlIgnore]
        public bool HasChanged { get; protected set; } = false;

        public override string ToString()
            => $"{GetType().GetFriendlyName()} ({SceneNode?.Name ?? Name ?? "<no name>"})";

        public event Action<TransformBase>? LocalMatrixChanged;
        public event Action<TransformBase>? InverseLocalMatrixChanged;
        public event Action<TransformBase>? WorldMatrixChanged;
        public event Action<TransformBase>? InverseWorldMatrixChanged;
        public event Action<TransformBase>? RenderWorldMatrixChanged;
        
        private float _selectionRadius = 0.01f;
        [YamlIgnore]
        public float SelectionRadius
        {
            get => _selectionRadius;
            set => SetField(ref _selectionRadius, value);
        }

        private Capsule _capsule = new(Vector3.Zero, Vector3.UnitY, 0.01f, 0.5f);
        [YamlIgnore]
        public Capsule Capsule
        {
            get => _capsule;
            set => SetField(ref _capsule, value);
        }

        protected TransformBase() : this(null) { }
        protected TransformBase(TransformBase? parent)
        {
            _sceneNode = null;
            Depth = parent?.Depth + 1 ?? 0;
            _children = new EventList<TransformBase>() { ThreadSafe = true };
            _children.PostAnythingAdded += ChildAdded;
            _children.PostAnythingRemoved += ChildRemoved;

            _localMatrix = new MatrixInfo { NeedsRecalc = true };
            _worldMatrix = new MatrixInfo { NeedsRecalc = true };
            _inverseLocalMatrix = new MatrixInfo { NeedsRecalc = true };
            _inverseWorldMatrix = new MatrixInfo { NeedsRecalc = true };

            RenderInfo = RenderInfo3D.New(this, new RenderCommandMethod3D((int)EDefaultRenderPass.OnTopForward, RenderDebug));
            RenderedObjects = GetDebugRenderInfo();
            DebugRender = Engine.Rendering.Settings.RenderTransformDebugInfo;

            SetParent(parent, false, true);
        }

        private void MakeCapsule()
        {
            Vector3 parentPos = Parent?.WorldTranslation ?? Vector3.Zero;
            Vector3 thisPos = WorldTranslation;
            Vector3 center = (parentPos + thisPos) / 2.0f;
            Vector3 dir = (thisPos - parentPos).Normalized();
            float halfHeight = Vector3.Distance(parentPos, thisPos) / 2.0f;
            Capsule = new Capsule(center, dir, SelectionRadius, halfHeight);
        }

        [YamlIgnore]
        public bool DebugRender
        {
            get => RenderedObjects.TryGet(0)?.IsVisible ?? false;
            set
            {
                foreach (var obj in RenderedObjects)
                    obj.IsVisible = value;
            }
        }

        protected RenderInfo3D RenderInfo { get; set; }

        protected virtual RenderInfo[] GetDebugRenderInfo()
        {
            RemakeCapsule();
            return [RenderInfo];
        }

        protected virtual void RenderDebug()
        {
            if (Engine.Rendering.State.IsShadowPass)
                return;
            
            var settings = Engine.Rendering.Settings;

            if (settings.RenderTransformLines)
                Engine.Rendering.Debug.RenderLine(
                    Parent?.RenderTranslation ?? Vector3.Zero,
                    RenderTranslation,
                    settings.TransformLineColor);

            if (settings.RenderTransformPoints)
                Engine.Rendering.Debug.RenderPoint(
                    RenderTranslation,
                    settings.TransformPointColor);

            if (settings.RenderTransformCapsules)
                Engine.Rendering.Debug.RenderCapsule(Capsule, settings.TransformCapsuleColor);

            if (settings.RenderTransformCullingVolumes)
            {
                var box = RenderInfo.LocalCullingVolume;
                if (box is not null)
                    Engine.Rendering.Debug.RenderBox(box.Value.HalfExtents, box.Value.Center, RenderInfo.CullingOffsetMatrix, false, ColorF4.Red);
            }
        }

        private void ChildAdded(TransformBase e)
            => e.Parent = this;

        private void ChildRemoved(TransformBase e)
            => e.Parent = null;

        private SceneNode? _sceneNode;
        /// <summary>
        /// This is the scene node that this transform is attached to and affects.
        /// Scene nodes are used to house components in relation to the scene hierarchy.
        /// </summary>
        public virtual SceneNode? SceneNode
        {
            get => _sceneNode;
            set => SetField(ref _sceneNode, value);
        }

        private int _depth = 0;
        [YamlIgnore]
        public int Depth 
        {
            get => _depth;
            private set => SetField(ref _depth, value);
        }

        private TransformBase? _parent;
        /// <summary>
        /// The parent of this transform.
        /// Will affect this transform's world matrix.
        /// </summary>
        public virtual TransformBase? Parent
        {
            get => _parent;
            set => SetField(ref _parent, value);
        }

        private EventList<TransformBase> _children;
        [YamlIgnore]
        public EventList<TransformBase> Children
        {
            get => _children;
            set
            {
                if (value is not null)
                    value.ThreadSafe = true;
                else
                    return;
                SetField(ref _children, value);
            }
        }

        public TransformBase[] ChildrenSerialized
        {
            get => [.. Children];
            set => Children = [.. value];
        }

        public void AddChild(TransformBase child, bool childPreservesWorldTransform, bool now)
        {
            if (child is null || child.Parent == this)
                return;
            child.SetParent(this, childPreservesWorldTransform, now);
        }

        public void RemoveChild(TransformBase child, bool now)
        {
            if (child is null || child.Parent != this)
                return;
            child.SetParent(null, false, now);
        }

        //TODO: multi-threaded deferred parent set doesn't work
        public void SetParent(TransformBase? newParent, bool preserveWorldTransform, bool now = false)
        {
            if (now)
            {
                if (preserveWorldTransform)
                {
                    var worldMatrix = WorldMatrix;
                    Parent = newParent;
                    DeriveWorldMatrix(worldMatrix);
                }
                else
                    Parent = newParent;
            }
            else
                _parentsToReassign.Enqueue((this, newParent, preserveWorldTransform));
        }

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(Parent):
                        _parent?._children.Remove(this);
                        break;
                    case nameof(Children):
                        _children.PostAnythingAdded -= ChildAdded;
                        _children.PostAnythingRemoved -= ChildRemoved;
                        lock (_children)
                        {
                            foreach (var child in _children)
                                if (child is not null)
                                {
                                    child.Parent = null;
                                    child.World = null;
                                }
                        }
                        break;
                }
            }
            return change;
        }
        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Parent):
                    if (_parent is not null)
                    {
                        Depth = _parent.Depth + 1;
                        _parent._children.Add(this);
                        World ??= _parent.World;
                    }
                    else
                        Depth = 0;
                    if (SceneNode is not null)
                        SceneNode.World = World;
                    MarkWorldModified();
                    break;
                case nameof(SceneNode):
                    var w = SceneNode?.World;
                    if (w is not null)
                        World = w;
                    break;
                case nameof(World):
                    foreach (var obj in RenderedObjects)
                        obj.WorldInstance = World;
                    MarkWorldModified();
                    if (SceneNode is not null)
                        SceneNode.World = World;
                    lock (_children)
                    {
                        foreach (var child in _children)
                            if (child is not null)
                                child.World = World;
                    }
                    break;
                case nameof(Children):
                    _children.PostAnythingAdded += ChildAdded;
                    _children.PostAnythingRemoved += ChildRemoved;
                    _children.ThreadSafe = true;
                    lock (_children)
                    {
                        foreach (var child in _children)
                            if (child is not null)
                            {
                                child.Parent = this;
                                child.World = World;
                            }
                    }
                    break;
                case nameof(SelectionRadius):
                    MakeCapsule();
                    break;
                case nameof(RenderMatrix):
                    OnRenderMatrixChanged();
                    break;
            }
        }

        protected virtual void OnRenderMatrixChanged()
        {
            //using var t = Engine.Profiler.Start();

            _inverseRenderMatrix = null;
            RenderWorldMatrixChanged?.Invoke(this);
        }

        private static readonly Queue<(TransformBase child, TransformBase? newParent, bool preserveWorldTransform)> _parentsToReassign = [];
        internal static void ProcessParentReassignments()
        {
            while (_parentsToReassign.TryDequeue(out (TransformBase child, TransformBase? newParent, bool preserveWorldTransform) t))
                if (t.child is not null && t.child.Parent != t.newParent)
                    t.child.SetParent(t.newParent, t.preserveWorldTransform, true);
        }

        /// <summary>
        /// Recalculates the local and world matrices for this transform.
        /// Children are not recalculated.
        /// Returns true if children need to be recalculated.
        /// </summary>
        public bool RecalculateMatrices(bool forceWorldRecalc = false, bool setRenderMatrixNow = false)
        {
            //try
            //{
            //    if (BeginVerifyWorldMatrix())
            //    {
                    bool recalcChildren = VerifyWorldMatrix(forceWorldRecalc);
                    if (setRenderMatrixNow || World is null)
                        SetRenderMatrix(WorldMatrix, false).Wait();
                    return recalcChildren;
                //}
                //else
                //    return false;
            //}
            //finally
            //{
            //    EndVerifyWorldMatrix();
            //}
        }

        public bool RecalculateInverseMatrices(bool forceInverseWorldRecalc = false)
        {
            //BeginVerifyWorldMatrix();
            //try
            //{
                return VerifyWorldInv(forceInverseWorldRecalc);
            //}
            //finally
            //{
            //    EndVerifyWorldMatrix();
            //}
        }

        /// <summary>
        /// Recalculates the local and world matrices for this transform and all children.
        /// If recalcChildrenNow is true, all children will be recalculated immediately.
        /// If false, they will be marked as dirty and recalculated at the end of the update.
        /// </summary>
        /// <param name="recalcChildrenNow"></param>
        /// <returns></returns>
        public virtual Task RecalculateMatrixHeirarchy(bool forceWorldRecalc, bool setRenderMatrixNow, bool parallel)
        {
            if (!RecalculateMatrices(forceWorldRecalc, setRenderMatrixNow))
                return Task.CompletedTask;

            if (parallel)
                return ParallelChildrenRecalc(setRenderMatrixNow);
            else
                return SequentialChildrenRecalc(setRenderMatrixNow);
        }

        private async Task SequentialChildrenRecalc(bool setRenderMatrixNow)
        {
            //var childrenCopy = RentChildrenCopy(out int c);
            var c = _children.Count;
            for (int i = 0; i < c; i++)
                await _children[i].RecalculateMatrixHeirarchy(true, setRenderMatrixNow, false);
            //ReturnChildrenCopy(childrenCopy);
        }

        private async Task ParallelChildrenRecalc(bool setRenderMatrixNow)
        {
            //var childrenCopy = RentChildrenCopy(out int c);
            var c = _children.Count;
            await Task.WhenAll(_children.Take(c).Select(child => child.RecalculateMatrixHeirarchy(true, setRenderMatrixNow, true)));
            //ReturnChildrenCopy(childrenCopy);
        }

        public Task SetRenderMatrix(Matrix4x4 matrix, bool recalcAllChildRenderMatrices = true)
        {
            RenderMatrix = matrix;
            if (recalcAllChildRenderMatrices)
                return RecalculateRenderMatrixHierarchy(Engine.Rendering.Settings.RecalcChildMatricesInParallel);
            else
                return Task.CompletedTask;
        }

        private Task RecalculateRenderMatrixHierarchy(bool parallel)
        {
            if (parallel)
                return ParallelChildrenRenderMatrixRecalc();
            else
                return SequentialChildrenRenderMatrixRecalc();
        }

        private async Task SequentialChildrenRenderMatrixRecalc()
        {
            //var childrenCopy = RentChildrenCopy(out int c);
            var c = _children.Count;
            for (int i = 0; i < c; i++)
            {
                TransformBase child = _children[i];
                await child.SetRenderMatrix(child.LocalMatrix * RenderMatrix, false);
            }
            //ReturnChildrenCopy(childrenCopy);
        }

        private TransformBase[] RentChildrenCopy(out int count)
        {
            lock (_children)
            {
                count = _children.Count;
                TransformBase[] childrenCopy = ArrayPool<TransformBase>.Shared.Rent(count);
                _children.CopyTo(childrenCopy, 0);
                return childrenCopy;
            }
        }

        private static void ReturnChildrenCopy(TransformBase[] copy)
            => ArrayPool<TransformBase>.Shared.Return(copy);

        private async Task ParallelChildrenRenderMatrixRecalc()
        {
            //var childrenCopy = RentChildrenCopy(out int c);
            var c = _children.Count;
            await Task.WhenAll(_children.Take(c).Select(child => child.SetRenderMatrix(child.LocalMatrix * RenderMatrix, true)));
            //ReturnChildrenCopy(childrenCopy);
        }

        public TransformBase? FindChild(string name, StringComparison comp = StringComparison.Ordinal)
        {
            lock (_children)
                return _children.FirstOrDefault(x => x.Name?.Equals(name, comp) ?? false);
        }
        public TransformBase? FindChild(Func<TransformBase, bool> predicate)
        {
            lock (_children)
                return _children.FirstOrDefault(predicate);
        }
        public TransformBase? FindChildStartsWith(string name, StringComparison comp = StringComparison.Ordinal)
        {
            lock (_children)
                return _children.FirstOrDefault(x => x.Name?.StartsWith(name, comp) ?? false);
        }
        public TransformBase? FindChildEndsWith(string name, StringComparison comp = StringComparison.Ordinal)
        {
            lock (_children)
                return _children.FirstOrDefault(x => x.Name?.EndsWith(name, comp) ?? false);
        }
        public TransformBase? FindChildContains(string name, StringComparison comp = StringComparison.Ordinal)
        {
            lock (_children)
                return _children.FirstOrDefault(x => x.Name?.Contains(name, comp) ?? false);
        }

        public TransformBase? GetChild(int index)
        {
            lock (_children)
                return _children.IndexInRange(index) ? _children[index] : null;
        }
        public TransformBase? FindDescendant(string name)
        {
            lock (_children)
            {
                TransformBase? child = _children.FirstOrDefault(x => x.Name == name);
                if (child is not null)
                    return child;
                foreach (TransformBase c in _children)
                {
                    child = c.FindDescendant(name);
                    if (child is not null)
                        return child;
                }
            }
            return null;
        }

        public TransformBase? FindDescendant(Func<TransformBase, bool> predicate)
        {
            lock (_children)
            {
                TransformBase? child = _children.FirstOrDefault(predicate);
                if (child is not null)
                    return child;
                foreach (TransformBase c in _children)
                {
                    child = c.FindDescendant(predicate);
                    if (child is not null)
                        return child;
                }
            }
            return null;
        }

        public TransformBase? TryGetChildAt(int index)
        {
            lock (_children)
                return _children.IndexInRange(index) ? _children[index] : null;
        }

        /// <summary>
        /// Returns the parent world matrix, or identity if no parent.
        /// </summary>
        public Matrix4x4 ParentWorldMatrix => Parent?.WorldMatrix ?? Matrix4x4.Identity;
        /// <summary>
        /// Returns the inverse of the parent world matrix, or identity if no parent.
        /// </summary>
        public Matrix4x4 ParentInverseWorldMatrix => Parent?.InverseWorldMatrix ?? Matrix4x4.Identity;

        public Quaternion ParentWorldRotation => Quaternion.CreateFromRotationMatrix(ParentWorldMatrix);
        public Quaternion ParentInverseWorldRotation => Quaternion.CreateFromRotationMatrix(ParentInverseWorldMatrix);

        /// <summary>
        /// Returns the parent bind matrix, or identity if no parent.
        /// </summary>
        public Matrix4x4 ParentBindMatrix => Parent?.BindMatrix ?? Matrix4x4.Identity;
        /// <summary>
        /// Returns the inverse of the parent bind matrix, or identity if no parent.
        /// </summary>
        public Matrix4x4 ParentInverseBindMatrix => Parent?.InverseBindMatrix ?? Matrix4x4.Identity;

        public Matrix4x4 ParentRenderMatrix => Parent?.RenderMatrix ?? Matrix4x4.Identity;
        public Matrix4x4 ParentInverseRenderMatrix => Parent?.InverseRenderMatrix ?? Matrix4x4.Identity;
        
        public Vector3 RenderForward => Vector3.TransformNormal(Globals.Forward, RenderMatrix);
        public Vector3 RenderUp => Vector3.TransformNormal(Globals.Up, RenderMatrix);
        public Vector3 RenderRight => Vector3.TransformNormal(Globals.Right, RenderMatrix);
        public Vector3 RenderTranslation => RenderMatrix.Translation;

        /// <summary>
        /// This transform's world up vector.
        /// </summary>
        public Vector3 WorldUp => Vector3.TransformNormal(Globals.Up, WorldMatrix);
        /// <summary>
        /// This transform's world right vector.
        /// </summary>
        public Vector3 WorldRight => Vector3.TransformNormal(Globals.Right, WorldMatrix);
        /// <summary>
        /// This transform's world forward vector.
        /// </summary>
        public Vector3 WorldForward => Vector3.TransformNormal(Globals.Forward, WorldMatrix);

        /// <summary>
        /// This transform's local up vector.
        /// </summary>
        public Vector3 LocalUp => Vector3.TransformNormal(Globals.Up, LocalMatrix);
        /// <summary>
        /// This transform's local right vector.
        /// </summary>
        public Vector3 LocalRight => Vector3.TransformNormal(Globals.Right, LocalMatrix);
        /// <summary>
        /// This transform's local forward vector.
        /// </summary>
        public Vector3 LocalForward => Vector3.TransformNormal(Globals.Forward, LocalMatrix);

        /// <summary>
        /// This transform's position in world space.
        /// </summary>
        public virtual Vector3 WorldTranslation => WorldMatrix.Translation;
        /// <summary>
        /// This transform's position in local space relative to the parent.
        /// </summary>
        public Vector3 LocalTranslation => LocalMatrix.Translation;

        public virtual Quaternion LocalRotation => Quaternion.CreateFromRotationMatrix(LocalMatrix);
        public virtual Quaternion WorldRotation => Quaternion.CreateFromRotationMatrix(WorldMatrix);

        public virtual Quaternion InverseLocalRotation => Quaternion.CreateFromRotationMatrix(InverseLocalMatrix);
        public virtual Quaternion InverseWorldRotation => Quaternion.CreateFromRotationMatrix(InverseWorldMatrix);

        public Vector3 LossyWorldScale => WorldMatrix.ExtractScale();

        private Matrix4x4 _renderMatrix = Matrix4x4.Identity;
        public Matrix4x4 RenderMatrix
        {
            get => _renderMatrix;
            internal set => SetFieldUnchecked(ref _renderMatrix, value);
        }

        private Matrix4x4? _inverseRenderMatrix = Matrix4x4.Identity;
        public Matrix4x4 InverseRenderMatrix => _inverseRenderMatrix ??= Matrix4x4.Invert(RenderMatrix, out var inverted) ? inverted : Matrix4x4.Identity;

        #region Local Matrix
        private readonly MatrixInfo _localMatrix;
        private readonly ReaderWriterLockSlim _localMatrixLock = new(LockRecursionPolicy.SupportsRecursion);
        /// <summary>
        /// This transform's local matrix relative to its parent.
        /// </summary>
        public Matrix4x4 LocalMatrix
        {
            get
            {
                //try
                //{
                    //if (_localMatrixLock.TryEnterUpgradeableReadLock(1))
                        VerifyLocalMatrix();

                    return _localMatrix.Matrix;
                //}
                //finally
                //{
                //    //ExitReadLock(_localMatrixLock);
                //}
            }
        }

        /// <summary>
        /// Ensures that the world matrix is up-to-date.
        /// If this is not called after values are changed and the matrix is invalidated,
        /// the matrix will not be recalculated until swap buffers is called or the matrix is specifically requested.
        /// </summary>
        public bool VerifyLocalMatrix(bool force = false)
        {
            if (!_localMatrix.NeedsRecalc && !force)
                return false;

            _localMatrix.NeedsRecalc = false;
            RecalcLocal();
            return true;
        }

        internal void RecalcLocal()
        {
            _localMatrixLock.EnterWriteLock();
            try
            {
                _localMatrix.Matrix = CreateLocalMatrix();
                _inverseLocalMatrix.NeedsRecalc = true;
                OnLocalMatrixChanged();
            }
            finally
            {
                _localMatrixLock.ExitWriteLock();
            }
        }

        protected virtual void OnLocalMatrixChanged()
            => LocalMatrixChanged?.Invoke(this);
        #endregion

        #region World Matrix
        private readonly MatrixInfo _worldMatrix;
        private readonly ReaderWriterLockSlim _worldMatrixLock = new(LockRecursionPolicy.SupportsRecursion);
        /// <summary>
        /// This transform's world matrix relative to the root of the scene (all ancestor transforms accounted for).
        /// </summary>
        public Matrix4x4 WorldMatrix
        {
            get
            {
                //try
                //{
                    //if (BeginVerifyWorldMatrix())
                        VerifyWorldMatrix();

                    return _worldMatrix.Matrix;
                //}
                //finally
                //{
                //    //EndVerifyWorldMatrix();
                //}
            }
        }

        private void EndVerifyWorldMatrix()
        {
            ExitReadLock(_worldMatrixLock);
            ExitReadLock(_localMatrixLock);
            RecursiveUnlockParentWorldMatrix();
        }

        private bool BeginVerifyWorldMatrix()
        {
            return true;
                //_worldMatrixLock.TryEnterUpgradeableReadLock(1) &&
                //_localMatrixLock.TryEnterUpgradeableReadLock(1) &&
                //RecursiveLockParentWorldMatrix();
        }

        /// <summary>
        /// Ensures that the world matrix is up-to-date.
        /// If this is not called after values are changed and the matrix is invalidated,
        /// the matrix will not be recalculated until swap buffers is called or the matrix is specifically requested.
        /// </summary>
        public bool VerifyWorldMatrix(bool force = false)
        {
            force |= VerifyLocalMatrix(force) | VerifyParentWorldMatrix();

            if (!_worldMatrix.NeedsRecalc && !force)
                return false;

            _worldMatrix.NeedsRecalc = false;
            RecalcWorld();
            return true;
        }

        private bool RecursiveLockParentWorldMatrix()
        {
            //if (Parent is null)
                return true;

            //bool locked = Parent._worldMatrixLock.TryEnterUpgradeableReadLock(1);
            //if (!locked)
            //    return false;

            //return Parent.RecursiveLockParentWorldMatrix();
        }
        private void RecursiveUnlockParentWorldMatrix()
        {
            if (Parent is null)
                return;

            ExitReadLock(Parent._worldMatrixLock);
            Parent.RecursiveUnlockParentWorldMatrix();
        }

        private bool VerifyParentWorldMatrix()
            => Parent?.VerifyWorldMatrix() ?? false;

        internal void RecalcWorld(/*bool allowSetLocal*/)
        {
            //_worldMatrixLock.EnterWriteLock();
            //try
            //{
                _worldMatrix.Matrix = CreateWorldMatrix();
                _inverseWorldMatrix.NeedsRecalc = true;

                //if (allowSetLocal/* && !_localMatrix.NeedsRecalc*/)
                //{
                //    _localMatrix.Matrix = GenerateLocalMatrixFromWorld();
                //    _inverseLocalMatrix.NeedsRecalc = true;
                //    OnLocalMatrixChanged();
                //}

                OnWorldMatrixChanged();
            //}
            //finally
            //{
            //    //_worldMatrixLock.ExitWriteLock();
            //}
        }

        private Matrix4x4 GenerateLocalMatrixFromWorld()
            => Parent is null || !Matrix4x4.Invert(Parent.WorldMatrix, out Matrix4x4 inverted)
                ? WorldMatrix
                : WorldMatrix * inverted;



        protected virtual void OnWorldMatrixChanged()
        {
            RemakeCapsule();
            World?.EnqueueRenderTransformChange(this);
            WorldMatrixChanged?.Invoke(this);
        }

        private void RemakeCapsule()
        {
            MakeCapsule();

            bool axisAligned = Engine.Rendering.Settings.TransformCullingIsAxisAligned;
            if (axisAligned)
            {
                RenderInfo.LocalCullingVolume = Capsule.GetAABB(true);
                RenderInfo.CullingOffsetMatrix = Matrix4x4.Identity;
            }
            else
            {
                RenderInfo.LocalCullingVolume = Capsule.GetAABB(false, true, out Quaternion dirToUp);
                RenderInfo.CullingOffsetMatrix = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(Quaternion.Inverse(dirToUp))) * Matrix4x4.CreateTranslation(Capsule.Center);
            }
        }

        #endregion

        #region Inverse Local Matrix
        private readonly MatrixInfo _inverseLocalMatrix;
        /// <summary>
        /// The inverse of this transform's local matrix.
        /// Calculated when requested if needed and cached until invalidated.
        /// </summary>
        public Matrix4x4 InverseLocalMatrix
        {
            get
            {
                //try
                //{
                    //if (_localMatrixLock.TryEnterUpgradeableReadLock(1))
                        VerifyLocalInv();

                    return _inverseLocalMatrix.Matrix;
                //}
                //finally
                //{
                //    //ExitReadLock(_localMatrixLock);
                //}
            }
        }

        private static void ExitReadLock(ReaderWriterLockSlim l)
        {
            if (l.IsReadLockHeld)
                l.ExitReadLock();
            else if (l.IsUpgradeableReadLockHeld)
                l.ExitUpgradeableReadLock();
        }

        private void VerifyLocalInv()
        {
            VerifyLocalMatrix();

            if (!_inverseLocalMatrix.NeedsRecalc)
                return;

            _inverseLocalMatrix.NeedsRecalc = false;
            RecalcLocalInv();
        }

        internal void RecalcLocalInv()
        {
            //_localMatrixLock.EnterWriteLock();
            //try
            //{
                if (!TryCreateInverseLocalMatrix(out Matrix4x4 inverted))
                    return;

                _inverseLocalMatrix.Matrix = inverted;
                OnInverseLocalMatrixChanged();
            //}
            //finally
            //{
            //    //_localMatrixLock.ExitWriteLock();
            //}
        }

        protected virtual void OnInverseLocalMatrixChanged()
            => InverseLocalMatrixChanged?.Invoke(this);

        #endregion

        #region Inverse World Matrix
        private readonly MatrixInfo _inverseWorldMatrix;
        /// <summary>
        /// The inverse of this transform's world matrix.
        /// Calculated when requested if needed and cached until invalidated.
        /// </summary>
        public Matrix4x4 InverseWorldMatrix
        {
            get
            {
                try
                {
                    //if (BeginVerifyWorldMatrix())
                        VerifyWorldInv();

                    return _inverseWorldMatrix.Matrix;
                }
                finally
                {
                    //EndVerifyWorldMatrix();
                }
            }
        }

        private bool VerifyWorldInv(bool force = false)
        {
            force |= VerifyWorldMatrix();

            if (!_inverseWorldMatrix.NeedsRecalc && !force)
                return false;

            _inverseWorldMatrix.NeedsRecalc = false;
            RecalcWorldInv();
            return true;
        }

        internal void RecalcWorldInv(/*bool allowSetLocal*/)
        {
            //_worldMatrixLock.EnterWriteLock();
            //try
            //{
                if (!TryCreateInverseWorldMatrix(out Matrix4x4 inverted))
                    return;

                _inverseWorldMatrix.Matrix = inverted;

                //if (allowSetLocal && !_inverseLocalMatrix.NeedsRecalc)
                //{
                //    _inverseLocalMatrix.Matrix = GenerateInverseLocalMatrixFromInverseWorld();
                //    OnInverseLocalMatrixChanged();
                //}

                OnInverseWorldMatrixChanged();
            //}
            //finally
            //{
            //    //_worldMatrixLock.ExitWriteLock();
            //}
        }

        private Matrix4x4 GenerateInverseLocalMatrixFromInverseWorld()
            => Parent is null || !Matrix4x4.Invert(Parent.WorldMatrix, out Matrix4x4 inverted)
                ? InverseWorldMatrix
                : inverted * InverseWorldMatrix;

        protected virtual void OnInverseWorldMatrixChanged()
            => InverseWorldMatrixChanged?.Invoke(this);
        #endregion

        #region Overridable Methods
        protected virtual Matrix4x4 CreateWorldMatrix()
            => Parent is null ? LocalMatrix : LocalMatrix * Parent.WorldMatrix;
        protected virtual bool TryCreateInverseLocalMatrix(out Matrix4x4 inverted)
            => Matrix4x4.Invert(LocalMatrix, out inverted);
        protected virtual bool TryCreateInverseWorldMatrix(out Matrix4x4 inverted)
            => Matrix4x4.Invert(WorldMatrix, out inverted);
        protected abstract Matrix4x4 CreateLocalMatrix();
        #endregion

        /// <summary>
        /// Marks the local matrix as modified, which will cause it to be recalculated on the next access.
        /// </summary>
        protected void MarkLocalModified()
        {
            _localMatrix.NeedsRecalc = true;
            _inverseLocalMatrix.NeedsRecalc = true;
            MarkWorldModified();
            HasChanged = true;
        }

        /// <summary>
        /// Marks the world matrix as modified, which will cause it to be recalculated on the next access.
        /// </summary>
        protected void MarkWorldModified()
        {
            _worldMatrix.NeedsRecalc = true;
            _inverseWorldMatrix.NeedsRecalc = true;
            World?.AddDirtyTransform(this);
            HasChanged = true;
        }

        ///// <summary>
        ///// Marks the inverse local matrix as modified, which will cause it to be recalculated on the next access.
        ///// </summary>
        //protected void MarkInverseLocalModified()
        //{
        //    _inverseLocalMatrix.Modified = true;
        //    MarkInverseWorldModified();
        //    World?.AddDirtyTransform(this);
        //}

        ///// <summary>
        ///// Marks the inverse world matrix as modified, which will cause it to be recalculated on the next access.
        ///// </summary>
        //protected void MarkInverseWorldModified()
        //{
        //    _inverseWorldMatrix.Modified = true;
        //    foreach (TransformBase child in Children)
        //        child.MarkInverseWorldModified();
        //    World?.AddDirtyTransform(this);
        //}

        //[Flags]
        //public enum ETransformTypeFlags
        //{
        //    None = 0,
        //    Local = 1,
        //    LocalInverse = 2,
        //    World = 4,
        //    WorldInverse = 8,
        //    All = 0xF,
        //}

        /// <summary>
        /// Called when the scene node this transform is attached to is activated in the scene.
        /// </summary>
        protected internal virtual void OnSceneNodeActivated()
        {
            //lock (Children)
            //    foreach (TransformBase child in Children)
            //        child.OnSceneNodeActivated();
        }
        /// <summary>
        /// Called when the scene node this transform is attached to is deactivated in the scene.
        /// </summary>
        protected internal virtual void OnSceneNodeDeactivated()
        {
            //lock (Children)
            //    foreach (TransformBase child in Children)
            //        child.OnSceneNodeDeactivated();
            //ClearTicks();
        }

        public int ChildCount
            => _children.Count;
        public TransformBase this[int index]
        {
            get => _children[index];
            set => _children[index] = value;
        }
        public int IndexOf(TransformBase item)
            => _children.IndexOf(item);
        public void Insert(int index, TransformBase item)
            => _children.Insert(index, item);
        public void RemoveAt(int index)
            => _children.RemoveAt(index);
        public void Clear()
            => _children.Clear();
        public bool Contains(TransformBase item)
            => _children.Contains(item);
        public void CopyTo(TransformBase[] array, int arrayIndex)
            => _children.CopyTo(array, arrayIndex);
        public void Add(TransformBase item)
            => _children.Add(item);
        public bool Remove(TransformBase item)
            => _children.Remove(item);

        /// <summary>
        /// Used to verify if the placement info for a child is the right type before being returned to the requester.
        /// </summary>
        /// <param name="childTransform"></param>
        public virtual void VerifyPlacementInfo(UITransform childTransform, ref UIChildPlacementInfo? placementInfo) { }
        /// <summary>
        /// Used by the physics system to derive a world matrix from a physics body into the components used by this transform.
        /// </summary>
        /// <param name="value"></param>
        public void DeriveWorldMatrix(Matrix4x4 value, bool networkSmoothed = false)
            => DeriveLocalMatrix(ParentInverseWorldMatrix * value, networkSmoothed);
        /// <summary>
        /// Derives components to create the local matrix from the given matrix.
        /// </summary>
        /// <param name="value"></param>
        public virtual void DeriveLocalMatrix(Matrix4x4 value, bool networkSmoothed = false) { }

        [UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code", Justification = "<Pending>")]
        public static Type[] TransformTypes { get; } = GetAllTransformTypes();

        [RequiresUnreferencedCode("This method is used to find all transform types in all assemblies in the current domain and should not be trimmed.")]
        private static Type[] GetAllTransformTypes() 
            => AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(x => x.GetExportedTypes())
                .Where(x => x.IsSubclassOf(typeof(TransformBase)))
                .ToArray();

        [RequiresUnreferencedCode("This method is used to find all transform types in all assemblies in the current domain and should not be trimmed.")]
        public static string[] GetFriendlyTransformTypeSelector()
            => TransformTypes.Select(FriendlyTransformName).ToArray();

        private static string FriendlyTransformName(Type x)
        {
            DisplayNameAttribute? name = x.GetCustomAttribute<DisplayNameAttribute>();
            return $"{name?.DisplayName ?? x.Name} ({x.Assembly.GetName()})";
        }

        public static TransformBase? FindCommonAncestor(TransformBase? a, TransformBase? b)
        {
            if (a is null || b is null)
                return null;

            var ancestorsA = new HashSet<TransformBase>();
            while (a is not null)
            {
                ancestorsA.Add(a);
                a = a.Parent;
            }

            while (b is not null)
            {
                if (ancestorsA.Contains(b))
                    return b;
                b = b.Parent;
            }

            return null;
        }
        public static TransformBase? FindCommonAncestor(params TransformBase[] transforms)
        {
            if (transforms.Length == 0)
                return null;

            TransformBase? commonAncestor = transforms.First();
            foreach (var bone in transforms)
            {
                commonAncestor = FindCommonAncestor(commonAncestor, bone);
                if (commonAncestor is null)
                    break;
            }
            return commonAncestor;
        }
    }
}