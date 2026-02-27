using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Rendering;
using XREngine.Scene;

namespace XREngine.Components.Lights
{
    /// <summary>
    /// Debug visualization modes for radiance cascades.
    /// </summary>
    public enum ERadianceCascadeDebugMode
    {
        /// <summary>No debug visualization.</summary>
        Off = 0,
        /// <summary>Visualize which cascade is being sampled (red=0, green=1, blue=2, yellow=3).</summary>
        CascadeIndex = 1,
        /// <summary>Visualize blend weights as grayscale.</summary>
        BlendWeights = 2,
    }

    /// <summary>
    /// Represents a hierarchy of pre-baked radiance volumes that increase in coverage with each cascade.
    /// </summary>
    public class RadianceCascadeComponent : XRComponent
    {
        public const int MaxCascades = 4;

        private readonly ObservableCollection<RadianceCascadeLevel> _cascades = [];
        private ColorF4 _tint = ColorF4.White;
        private float _intensity = 1.0f;
        private bool _cascadesEnabled = true;
        
        // New settings for improvements
        private float _temporalBlendFactor = 0.85f;
        private float _normalOffsetScale = 1.5f;
        private bool _halfResolution = false;
        private ERadianceCascadeDebugMode _debugMode = ERadianceCascadeDebugMode.Off;

        private XRWorldInstance? _registeredWorld;

        public RadianceCascadeComponent()
        {
            _cascades.CollectionChanged += HandleCollectionChanged;
        }

        /// <summary>
        /// Editable collection of cascades ordered from highest to lowest resolution.
        /// </summary>
        [Category("Radiance Cascades")]
        public ObservableCollection<RadianceCascadeLevel> Cascades => _cascades;

        /// <summary>
        /// Global tint applied to every cascade sample.
        /// </summary>
        [Category("Radiance Cascades")]
        public ColorF4 Tint
        {
            get => _tint;
            set => SetField(ref _tint, value);
        }

        /// <summary>
        /// Global scalar intensity applied after tinting.
        /// </summary>
        [Category("Radiance Cascades")]
        public float Intensity
        {
            get => _intensity;
            set => SetField(ref _intensity, MathF.Max(0.0f, value));
        }

        /// <summary>
        /// Allows temporarily disabling all cascades without removing them.
        /// </summary>
        [Category("Radiance Cascades")]
        public bool CascadesEnabled
        {
            get => _cascadesEnabled;
            set => SetField(ref _cascadesEnabled, value);
        }

        /// <summary>
        /// Temporal blend factor for GI stability. 0 = no temporal blending, 0.95 = heavy smoothing.
        /// Higher values reduce flickering but may cause ghosting on fast movement.
        /// </summary>
        [Category("Radiance Cascades - Quality")]
        [Description("Temporal blend factor (0-0.95). Higher values reduce flickering but may cause ghosting.")]
        public float TemporalBlendFactor
        {
            get => _temporalBlendFactor;
            set => SetField(ref _temporalBlendFactor, MathF.Max(0.0f, MathF.Min(0.95f, value)));
        }

        /// <summary>
        /// Scale for normal-based sampling offset. Reduces light leaking through thin geometry.
        /// </summary>
        [Category("Radiance Cascades - Quality")]
        [Description("Normal offset scale (0-5). Higher values reduce light leaking but may cause banding.")]
        public float NormalOffsetScale
        {
            get => _normalOffsetScale;
            set => SetField(ref _normalOffsetScale, MathF.Max(0.0f, MathF.Min(5.0f, value)));
        }

        /// <summary>
        /// Render GI at half resolution for improved performance. Uses depth-aware upscaling.
        /// </summary>
        [Category("Radiance Cascades - Performance")]
        [Description("Render at half resolution for better performance with minimal quality loss.")]
        public bool HalfResolution
        {
            get => _halfResolution;
            set => SetField(ref _halfResolution, value);
        }

        /// <summary>
        /// Debug visualization mode for troubleshooting cascade selection.
        /// </summary>
        [Category("Radiance Cascades - Debug")]
        [Description("Visualization mode for debugging cascade selection and blending.")]
        public ERadianceCascadeDebugMode DebugMode
        {
            get => _debugMode;
            set => SetField(ref _debugMode, value);
        }

        /// <summary>
        /// True when at least one cascade has a valid volume and the component is active.
        /// </summary>
        [Browsable(false)]
        public bool HasValidCascades
            => _cascadesEnabled && IsActiveInHierarchy && _cascades.Any(c => c.Enabled && c.RadianceTexture is not null);

        /// <summary>
        /// Returns the active cascades limited to the supported maximum.
        /// </summary>
        public IReadOnlyList<RadianceCascadeLevel> GetActiveCascades()
            => _cascades.Where(c => c.Enabled && c.RadianceTexture is not null).Take(MaxCascades).ToArray();

        /// <summary>
        /// Computes a transform that converts world space into local cascade space.
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
                case nameof(CascadesEnabled):
                    RefreshRegistration();
                    break;
            }
        }

        protected override void OnDestroying()
        {
            base.OnDestroying();
            Unregister();
        }

        private void HandleCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems is not null)
            {
                foreach (RadianceCascadeLevel item in e.OldItems)
                    item.PropertyChanged -= HandleCascadePropertyChanged;
            }

            if (e.NewItems is not null)
            {
                foreach (RadianceCascadeLevel item in e.NewItems)
                    item.PropertyChanged += HandleCascadePropertyChanged;
            }

            RefreshRegistration();
        }

        private void HandleCascadePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(RadianceCascadeLevel.RadianceTexture):
                case nameof(RadianceCascadeLevel.Enabled):
                    RefreshRegistration();
                    break;
            }
        }

        private void RefreshRegistration()
        {
            var world = World;
            bool shouldRegister = world is not null && HasValidCascades;

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
        /// Tracks radiance cascade components per world so render passes can fetch active data quickly.
        /// </summary>
        internal static class Registry
        {
            private static readonly Dictionary<XRWorldInstance, List<RadianceCascadeComponent>> s_perWorld = [];
            private static readonly object s_lock = new();

            public static void Register(XRWorldInstance world, RadianceCascadeComponent component)
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

            public static void Unregister(XRWorldInstance world, RadianceCascadeComponent component)
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

            public static bool TryGetFirstActive(XRWorldInstance world, out RadianceCascadeComponent? component)
            {
                lock (s_lock)
                {
                    if (s_perWorld.TryGetValue(world, out var list))
                    {
                        for (int i = 0; i < list.Count; i++)
                        {
                            var candidate = list[i];
                            if (candidate.HasValidCascades)
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

    /// <summary>
    /// Single cascade definition containing its volume and coverage data.
    /// </summary>
    public class RadianceCascadeLevel : INotifyPropertyChanged
    {
        private XRTexture3D? _radianceTexture;
        private Vector3 _halfExtents = new(5.0f);
        private float _intensity = 1.0f;
        private bool _enabled = true;

        [Category("Radiance Cascades")]
        public XRTexture3D? RadianceTexture
        {
            get => _radianceTexture;
            set => SetField(ref _radianceTexture, value);
        }

        [Category("Radiance Cascades")]
        public Vector3 HalfExtents
        {
            get => _halfExtents;
            set => SetField(ref _halfExtents, value);
        }

        [Category("Radiance Cascades")]
        public float Intensity
        {
            get => _intensity;
            set => SetField(ref _intensity, MathF.Max(0.0f, value));
        }

        [Category("Radiance Cascades")]
        public bool Enabled
        {
            get => _enabled;
            set => SetField(ref _enabled, value);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return;

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}