using System.Collections.Generic;
using System.ComponentModel;
using System.Numerics;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Rendering;

namespace XREngine.Components.Scene.Volumes
{
    /// <summary>
    /// Bounded local-space volume that contributes volumetric fog during the post-process pass.
    /// </summary>
    [Description("A bounded local-space volume that contributes volumetric fog during post-processing.")]
    public class VolumetricFogVolumeComponent : XRComponent
    {
        private Vector3 _halfExtents = new(5.0f);
        private ColorF3 _scatteringColor = new(0.75f, 0.82f, 0.95f);
        private float _density = 0.04f;
        private float _noiseScale = 0.125f;
        private Vector3 _noiseOffset = Vector3.Zero;
        private Vector3 _noiseVelocity = new(0.0f, 0.05f, 0.0f);
        private float _noiseThreshold = 0.35f;
        private float _noiseAmount = 0.7f;
        private float _edgeFade = 0.2f;
        private float _anisotropy = 0.2f;
        private float _lightContribution = 1.0f;
        private int _priority;
        private bool _volumeEnabled = true;

        private XRWorldInstance? _registeredWorld;

        [Browsable(false)]
        public bool HasRenderableVolume
            => _volumeEnabled
            && IsActiveInHierarchy
            && _density > 0.0f
            && _halfExtents.X > 0.0f
            && _halfExtents.Y > 0.0f
            && _halfExtents.Z > 0.0f;

        [Category("Volumetric Fog")]
        public Vector3 HalfExtents
        {
            get => _halfExtents;
            set => SetField(ref _halfExtents, new Vector3(
                MathF.Max(0.001f, value.X),
                MathF.Max(0.001f, value.Y),
                MathF.Max(0.001f, value.Z)));
        }

        [Category("Volumetric Fog")]
        public ColorF3 ScatteringColor
        {
            get => _scatteringColor;
            set => SetField(ref _scatteringColor, value);
        }

        [Category("Volumetric Fog")]
        public float Density
        {
            get => _density;
            set => SetField(ref _density, MathF.Max(0.0f, value));
        }

        [Category("Volumetric Fog")]
        public float NoiseScale
        {
            get => _noiseScale;
            set => SetField(ref _noiseScale, MathF.Max(0.0f, value));
        }

        [Category("Volumetric Fog")]
        public Vector3 NoiseOffset
        {
            get => _noiseOffset;
            set => SetField(ref _noiseOffset, value);
        }

        [Category("Volumetric Fog")]
        public Vector3 NoiseVelocity
        {
            get => _noiseVelocity;
            set => SetField(ref _noiseVelocity, value);
        }

        [Category("Volumetric Fog")]
        public float NoiseThreshold
        {
            get => _noiseThreshold;
            set => SetField(ref _noiseThreshold, Math.Clamp(value, 0.0f, 1.0f));
        }

        [Category("Volumetric Fog")]
        public float NoiseAmount
        {
            get => _noiseAmount;
            set => SetField(ref _noiseAmount, Math.Clamp(value, 0.0f, 1.0f));
        }

        [Category("Volumetric Fog")]
        public float EdgeFade
        {
            get => _edgeFade;
            set => SetField(ref _edgeFade, Math.Clamp(value, 0.0f, 1.0f));
        }

        [Category("Volumetric Fog")]
        public float Anisotropy
        {
            get => _anisotropy;
            set => SetField(ref _anisotropy, Math.Clamp(value, -0.95f, 0.95f));
        }

        [Category("Volumetric Fog")]
        public float LightContribution
        {
            get => _lightContribution;
            set => SetField(ref _lightContribution, MathF.Max(0.0f, value));
        }

        [Category("Volumetric Fog")]
        public int Priority
        {
            get => _priority;
            set => SetField(ref _priority, value);
        }

        [Category("Volumetric Fog")]
        public bool VolumeEnabled
        {
            get => _volumeEnabled;
            set => SetField(ref _volumeEnabled, value);
        }

        public bool TryGetWorldToLocal(out Matrix4x4 worldToLocal)
            => Matrix4x4.Invert(Transform.RenderMatrix, out worldToLocal);

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            RefreshRegistration();
        }

        protected override void OnComponentDeactivated()
        {
            Unregister();
            base.OnComponentDeactivated();
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);

            switch (propName)
            {
                case nameof(World):
                case nameof(IsActive):
                case nameof(VolumeEnabled):
                    RefreshRegistration();
                    break;
            }
        }

        protected override void OnDestroying()
        {
            base.OnDestroying();
            Unregister();
        }

        private void RefreshRegistration()
        {
            var world = WorldAs<XRWorldInstance>();
            bool shouldRegister = world is not null && IsActiveInHierarchy && _volumeEnabled;

            if (_registeredWorld is not null && (!shouldRegister || _registeredWorld != world))
                Unregister();

            if (shouldRegister && _registeredWorld is null && world is not null)
            {
                Registry.Register(world, this);
                _registeredWorld = world;
            }
        }

        private void Unregister()
        {
            if (_registeredWorld is null)
                return;

            Registry.Unregister(_registeredWorld, this);
            _registeredWorld = null;
        }

        internal static class Registry
        {
            private static readonly Dictionary<XRWorldInstance, List<VolumetricFogVolumeComponent>> s_perWorld = new();
            private static readonly object s_lock = new();

            public static void Register(XRWorldInstance world, VolumetricFogVolumeComponent component)
            {
                lock (s_lock)
                {
                    if (!s_perWorld.TryGetValue(world, out var list))
                    {
                        list = [];
                        s_perWorld[world] = list;
                    }

                    if (!list.Contains(component))
                        list.Add(component);
                }
            }

            public static void Unregister(XRWorldInstance world, VolumetricFogVolumeComponent component)
            {
                lock (s_lock)
                {
                    if (!s_perWorld.TryGetValue(world, out var list))
                        return;

                    list.Remove(component);
                    if (list.Count == 0)
                        s_perWorld.Remove(world);
                }
            }

            public static int CopyActive(XRWorldInstance world, Span<VolumetricFogVolumeComponent?> destination)
            {
                destination.Clear();

                lock (s_lock)
                {
                    if (!s_perWorld.TryGetValue(world, out var list))
                        return 0;

                    int count = 0;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var candidate = list[i];
                        if (!candidate.HasRenderableVolume)
                            continue;

                        int insertIndex = 0;
                        while (insertIndex < count && destination[insertIndex] is { } existing && existing.Priority >= candidate.Priority)
                            insertIndex++;

                        if (insertIndex >= destination.Length)
                            continue;

                        int lastIndex = Math.Min(count, destination.Length - 1);
                        for (int shift = lastIndex; shift > insertIndex; shift--)
                            destination[shift] = destination[shift - 1];

                        destination[insertIndex] = candidate;
                        if (count < destination.Length)
                            count++;
                    }

                    return count;
                }
            }
        }
    }
}