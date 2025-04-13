using Extensions;
using System.Collections.Concurrent;
using System.Numerics;
using XREngine.Animation;
using XREngine.Scene.Components.Animation;

namespace XREngine.Components
{
    public class AnimStateMachineComponent : XRComponent
    {
        private bool _animatePhysics = false;
        public bool AnimatePhysics
        {
            get => _animatePhysics;
            set => SetField(ref _animatePhysics, value);
        }

        private bool _applyRootMotion;
        private Vector3 _pivotPosition;
        private Vector3 _deltaPosition;

        private HumanoidComponent? _skeleton;
        public HumanoidComponent? Skeleton
        {
            get => _skeleton;
            set => SetField(ref _skeleton, value);
        }

        private EventList<AnimLayer> _layers = [];
        public EventList<AnimLayer> Layers
        {
            get => _layers;
            set => SetField(ref _layers, value);
        }

        private ConcurrentDictionary<string, SkeletalAnimation> _animationTable = new();
        public ConcurrentDictionary<string, SkeletalAnimation> AnimationTable
        {
            get => _animationTable;
            set => SetField(ref _animationTable, value);
        }

        public AnimStateMachineComponent()
        {
            Skeleton = null;
        }
        public AnimStateMachineComponent(HumanoidComponent skeleton)
        {
            Skeleton = skeleton;
        }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();

            foreach (var layer in Layers)
                layer?.Initialize(this);
            
            RegisterTick(ETickGroup.Normal, ETickOrder.Animation, Tick);
        }
        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();

            UnregisterTick(ETickGroup.Normal, ETickOrder.Animation, Tick);

            foreach (var layer in Layers)
                layer?.Deinitialize();
        }

        protected internal void Tick()
        {
            for (int i = 0; i < Layers.Count; ++i)
            {
                var layer = Layers[i];
                if (layer is null)
                    continue;

                layer.Tick(Engine.Delta, Skeleton, Variables);
            }
        }

        private EventDictionary<string, AnimVar> _variables = [];
        public EventDictionary<string, AnimVar> Variables
        {
            get => _variables;
            set => SetField(ref _variables, value);
        }
        public bool ApplyRootMotion
        {
            get => _applyRootMotion;
            set => SetField(ref _applyRootMotion, value);
        }
        public Vector3 PivotPosition
        {
            get => _pivotPosition;
            set => SetField(ref _pivotPosition, value);
        }
        public Vector3 DeltaPosition
        {
            get => _deltaPosition;
            set => SetField(ref _deltaPosition, value);
        }

        public void SetInt(string index, int value)
        {
            if (Variables.TryGetValue(index, out AnimVar? var))
                var.SetInt(value);
        }

        public void SetFloat(string index, float value)
        {
            if (Variables.TryGetValue(index, out AnimVar? var))
                var.SetFloat(value);
        }

        public void SetBool(string index, bool value)
        {
            if (Variables.TryGetValue(index, out AnimVar? var))
                var.SetBool(value);
        }

        public AnimStateTransition? GetCurrentTransition(int layerIndex)
        {
            if (layerIndex < 0 || layerIndex >= Layers.Count)
                return null;
            var layer = Layers[layerIndex];
            if (layer is null)
                return null;
            return layer.CurrentTransition;
        }
    }
}
