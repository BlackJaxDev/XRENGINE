using Extensions;
using System.Drawing;
using System.Numerics;
using XREngine.Data;
using XREngine.Rendering.Physics.Physx;
using XREngine.Scene;
using XREngine.Components.Animation;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Scene.Transforms
{
    /// <summary>
    /// Typical 3rd person boom camera transform, with a trace sphere to prevent clipping through walls.
    /// </summary>
    public class BoomTransform : TransformBase, IRenderable
    {
        public delegate void DelDistanceChange(float newLength);
        public event DelDistanceChange? CurrentDistanceChanged;

        private float _currentLength = 0.0f;
        private float _maxLength = 3.0f;
        private float _zoomOutSpeed = 10.0f;
        private IPhysicsGeometry.Sphere _traceSphere = new(0.2f);
        private bool _zoomOutAffectedByTimeDilation = true;

        /// <summary>
        /// How big the trace sphere is.
        /// Should be at least the size of the camera's near clip plane.
        /// </summary>
        public unsafe float TraceRadius
        {
            get => _traceSphere.Radius;
            set => SetField(ref _traceSphere.Radius, value);
        }

        /// <summary>
        /// Maximum length the boom can extend.
        /// </summary>
        public float MaxLength
        {
            get => _maxLength;
            set => SetField(ref _maxLength, value);
        }

        /// <summary>
        /// How fast the boom zooms out when it's not obstructed.
        /// </summary>
        public float ZoomOutSpeed
        {
            get => _zoomOutSpeed;
            set => SetField(ref _zoomOutSpeed, value);
        }

        /// <summary>
        /// If true, the zoom out speed will be affected by time dilation.
        /// </summary>
        public bool ZoomOutAffectedByTimeDilation
        {
            get => _zoomOutAffectedByTimeDilation;
            set => SetField(ref _zoomOutAffectedByTimeDilation, value);
        }

        protected override unsafe void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(MaxLength):
                    _currentLength = _currentLength.Clamp(0.0f, MaxLength);
                    break;
            }
        }

        //protected override void RenderDebug()
        //{
        //    //base.RenderDebug();

        //    if (Engine.Rendering.State.IsShadowPass)
        //        return;

        //    Engine.Rendering.Debug.RenderSphere(WorldTranslation, TraceRadius, false, Color.Black);
        //    Engine.Rendering.Debug.RenderLine(ParentWorldMatrix.Translation, WorldTranslation, Color.Black);
        //}

        protected override Matrix4x4 CreateLocalMatrix()
            => Matrix4x4.CreateTranslation(0.0f, 0.0f, _currentLength);

        protected internal override void OnSceneNodeActivated()
        {
            base.OnSceneNodeActivated();
            RegisterTick(ETickGroup.Late, (int)ETickOrder.Scene, Tick);
        }
        //protected internal override void OnSceneNodeDeactivated()
        //{
        //    base.OnSceneNodeDeactivated();
        //    UnregisterTick(ETickGroup.PostPhysics, (int)ETickOrder.Scene, Tick);
        //}

        private readonly SortedDictionary<float, List<(XRComponent? item, object? data)>> _traceOutput = [];

        private PhysxScene.PhysxQueryFilter _queryFilter = new()
        {
            Flags = MagicPhysX.PxQueryFlags.Static,
        };
        public PhysxScene.PhysxQueryFilter QueryFilter
        {
            get => _queryFilter;
            set => SetField(ref _queryFilter, value);
        }

        private LayerMask _layerMask = LayerMask.GetMask(DefaultLayers.Default);
        public LayerMask LayerMask
        {
            get => _layerMask;
            set => SetField(ref _layerMask, value);
        }

        private void Tick()
        {
            float newLength;
            lock (_traceOutput)
            {
                _traceOutput.Clear();
                World?.PhysicsScene?.SweepSingle(
                    _traceSphere,
                    (ParentWorldMatrix.Translation, Quaternion.Identity),
                    Vector3.Transform(Globals.Backward, Parent?.WorldRotation ?? Quaternion.Identity),
                    MaxLength,
                    _layerMask,
                    _queryFilter,
                    _traceOutput);
                if (_traceOutput.Count == 0)
                    newLength = MaxLength;
                else
                    newLength = _traceOutput.Keys.First();
            }

            if (float.IsNaN(newLength))
                return;

            if (newLength < _currentLength)
                _currentLength = newLength; //Moving closer to the character, meaning something is obscuring the view. Need to jump to the right position.
            else //Nothing is now obscuring the view, so we can lerp out quickly to give the appearance of a clean camera zoom out
                _currentLength = Interp.Lerp(_currentLength, newLength, ZoomOutAffectedByTimeDilation ? Engine.Delta : Engine.UndilatedDelta, ZoomOutSpeed);

            MarkLocalModified();
            CurrentDistanceChanged?.Invoke(_currentLength);
        }
    }
}
