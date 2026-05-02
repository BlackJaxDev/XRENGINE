using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Numerics;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Rendering;
using XREngine.Scene;

namespace XREngine.Components.Lights
{
    /// <summary>
    /// Represents a baked light volume for voxel-based GI lighting.
    /// A volume provides a 3D irradiance texture and transform used to sample GI.
    /// </summary>
    public class LightVolumeComponent : XRComponent
    {
        private XRTexture3D? _volumeTexture;
        private Vector3 _halfExtents = new(5.0f);
        private ColorF4 _tint = ColorF4.White;
        private float _intensity = 1.0f;
        private bool _volumeEnabled = true;

        private IRuntimeRenderWorld? _registeredWorld;

        /// <summary>
        /// True when this component can contribute a sampled irradiance volume to its world.
        /// </summary>
        [Browsable(false)]
        public bool HasValidVolume => _volumeEnabled && _volumeTexture is not null && IsActiveInHierarchy;

        /// <summary>
        /// Baked 3D irradiance data sampled by GI/light-volume render passes.
        /// </summary>
        [Category("Light Volumes")]
        public XRTexture3D? VolumeTexture
        {
            get => _volumeTexture;
            set => SetField(ref _volumeTexture, value);
        }

        /// <summary>
        /// Half-size of the volume bounds in local space units.
        /// </summary>
        [Category("Light Volumes")]
        public Vector3 HalfExtents
        {
            get => _halfExtents;
            set => SetField(ref _halfExtents, value);
        }

        /// <summary>
        /// Multiplicative color applied while sampling the volume texture.
        /// </summary>
        [Category("Light Volumes")]
        public ColorF4 Tint
        {
            get => _tint;
            set => SetField(ref _tint, value);
        }

        /// <summary>
        /// Scalar brightness multiplier applied after tinting.
        /// </summary>
        [Category("Light Volumes")]
        public float Intensity
        {
            get => _intensity;
            set => SetField(ref _intensity, MathF.Max(0.0f, value));
        }

        /// <summary>
        /// Enables this volume without clearing the assigned texture or bounds.
        /// </summary>
        [Category("Light Volumes")]
        public bool VolumeEnabled
        {
            get => _volumeEnabled;
            set => SetField(ref _volumeEnabled, value);
        }

        /// <summary>
        /// Computes a transform that converts world space into local volume space.
        /// </summary>
        public bool TryGetWorldToLocal(out Matrix4x4 worldToLocal)
            => Matrix4x4.Invert(Transform.RenderMatrix, out worldToLocal);

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
            IRuntimeRenderWorld? world = WorldAs<IRuntimeRenderWorld>();
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

        /// <summary>
        /// Tracks light volume components per world so render passes can query active data quickly.
        /// </summary>
        internal static class Registry
        {
            private static readonly Dictionary<IRuntimeRenderWorld, List<LightVolumeComponent>> s_perWorld = new();
            private static readonly object s_lock = new();

            public static void Register(IRuntimeRenderWorld world, LightVolumeComponent component)
            {
                lock (s_lock)
                {
                    if (!s_perWorld.TryGetValue(world, out List<LightVolumeComponent>? list))
                    {
                        list = [];
                        s_perWorld[world] = list;
                    }

                    if (!list.Contains(component))
                        list.Add(component);
                }
            }

            public static void Unregister(IRuntimeRenderWorld world, LightVolumeComponent component)
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

            public static bool TryGetFirstActive(IRuntimeRenderWorld world, out LightVolumeComponent? component)
            {
                lock (s_lock)
                {
                    if (s_perWorld.TryGetValue(world, out List<LightVolumeComponent>? list))
                    {
                        for (int i = 0; i < list.Count; i++)
                        {
                            LightVolumeComponent candidate = list[i];
                            if (candidate.HasValidVolume)
                            {
                                component = candidate;
                                return true;
                            }
                        }
                    }
                }

                component = null;
                return false;
            }
        }
    }
}
