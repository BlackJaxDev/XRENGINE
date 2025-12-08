using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Capture.Lights;
using XREngine.Data.Vectors;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Capture
{
    /// <summary>
    /// Spawns a grid of child nodes each with a light probe when play begins, and cleans them up when play ends.
    /// </summary>
    public class LightProbeGridSpawnerComponent : XRComponent
    {
        private readonly List<SceneNode> _spawnedNodes = new();

        private IVector3 _probeCounts = new(2, 2, 2);
        /// <summary>
        /// Number of probes to create along each axis. Values less than one are clamped to one.
        /// </summary>
        [Category("Grid")]
        public IVector3 ProbeCounts
        {
            get => _probeCounts;
            set
            {
                var clamped = new IVector3(
                    Math.Max(1, value.X),
                    Math.Max(1, value.Y),
                    Math.Max(1, value.Z));
                if (SetField(ref _probeCounts, clamped))
                    RegenerateGridIfSpawned();
            }
        }

        private Vector3 _spacing = new(1f, 1f, 1f);
        /// <summary>
        /// Distance in world units between probes along each axis. Components are clamped to a minimum positive value.
        /// </summary>
        [Category("Grid")]
        public Vector3 Spacing
        {
            get => _spacing;
            set
            {
                var clamped = new Vector3(
                    MathF.Max(0.001f, value.X),
                    MathF.Max(0.001f, value.Y),
                    MathF.Max(0.001f, value.Z));
                if (SetField(ref _spacing, clamped))
                    RegenerateGridIfSpawned();
            }
        }

        private Vector3 _offset = Vector3.Zero;
        /// <summary>
        /// Local-space offset to apply to the generated grid origin.
        /// </summary>
        [Category("Grid")]
        public Vector3 Offset
        {
            get => _offset;
            set
            {
                if (SetField(ref _offset, value))
                    RegenerateGridIfSpawned();
            }
        }

        private bool _realtimeCapture = false;
        private TimeSpan? _realTimeCaptureUpdateInterval = TimeSpan.FromMilliseconds(100.0);
        private uint _irradianceResolution = 32;
        private LightProbeComponent.EInfluenceShape _influenceShape = LightProbeComponent.EInfluenceShape.Sphere;
        private Vector3 _influenceOffset = Vector3.Zero;
        private float _influenceSphereInnerRadius = 0.0f;
        private float _influenceSphereOuterRadius = 5.0f;
        private Vector3 _influenceBoxInnerExtents = Vector3.Zero;
        private Vector3 _influenceBoxOuterExtents = new(5.0f, 5.0f, 5.0f);
        private bool _parallaxCorrectionEnabled = false;
        private Vector3 _proxyBoxCenterOffset = Vector3.Zero;
        private Vector3 _proxyBoxHalfExtents = Vector3.One;
        private Quaternion _proxyBoxRotation = Quaternion.Identity;

        [Category("Probe Defaults")]
        public bool RealtimeCapture
        {
            get => _realtimeCapture;
            set
            {
                if (SetField(ref _realtimeCapture, value))
                    ApplyDefaultsToExistingProbes();
            }
        }

        [Category("Probe Defaults")]
        public TimeSpan? RealTimeCaptureUpdateInterval
        {
            get => _realTimeCaptureUpdateInterval;
            set
            {
                if (SetField(ref _realTimeCaptureUpdateInterval, value))
                    ApplyDefaultsToExistingProbes();
            }
        }

        [Category("Probe Defaults")]
        public uint IrradianceResolution
        {
            get => _irradianceResolution;
            set
            {
                if (SetField(ref _irradianceResolution, value))
                    ApplyDefaultsToExistingProbes();
            }
        }

        [Category("Probe Defaults")]
        public LightProbeComponent.EInfluenceShape InfluenceShape
        {
            get => _influenceShape;
            set
            {
                if (SetField(ref _influenceShape, value))
                    ApplyDefaultsToExistingProbes();
            }
        }

        [Category("Probe Defaults")]
        public Vector3 InfluenceOffset
        {
            get => _influenceOffset;
            set
            {
                if (SetField(ref _influenceOffset, value))
                    ApplyDefaultsToExistingProbes();
            }
        }

        [Category("Probe Defaults")]
        public float InfluenceSphereInnerRadius
        {
            get => _influenceSphereInnerRadius;
            set
            {
                if (SetField(ref _influenceSphereInnerRadius, value))
                    ApplyDefaultsToExistingProbes();
            }
        }

        [Category("Probe Defaults")]
        public float InfluenceSphereOuterRadius
        {
            get => _influenceSphereOuterRadius;
            set
            {
                if (SetField(ref _influenceSphereOuterRadius, value))
                    ApplyDefaultsToExistingProbes();
            }
        }

        [Category("Probe Defaults")]
        public Vector3 InfluenceBoxInnerExtents
        {
            get => _influenceBoxInnerExtents;
            set
            {
                if (SetField(ref _influenceBoxInnerExtents, value))
                    ApplyDefaultsToExistingProbes();
            }
        }

        [Category("Probe Defaults")]
        public Vector3 InfluenceBoxOuterExtents
        {
            get => _influenceBoxOuterExtents;
            set
            {
                if (SetField(ref _influenceBoxOuterExtents, value))
                    ApplyDefaultsToExistingProbes();
            }
        }

        [Category("Probe Defaults")]
        public bool ParallaxCorrectionEnabled
        {
            get => _parallaxCorrectionEnabled;
            set
            {
                if (SetField(ref _parallaxCorrectionEnabled, value))
                    ApplyDefaultsToExistingProbes();
            }
        }

        [Category("Probe Defaults")]
        public Vector3 ProxyBoxCenterOffset
        {
            get => _proxyBoxCenterOffset;
            set
            {
                if (SetField(ref _proxyBoxCenterOffset, value))
                    ApplyDefaultsToExistingProbes();
            }
        }

        [Category("Probe Defaults")]
        public Vector3 ProxyBoxHalfExtents
        {
            get => _proxyBoxHalfExtents;
            set
            {
                if (SetField(ref _proxyBoxHalfExtents, value))
                    ApplyDefaultsToExistingProbes();
            }
        }

        [Category("Probe Defaults")]
        public Quaternion ProxyBoxRotation
        {
            get => _proxyBoxRotation;
            set
            {
                if (SetField(ref _proxyBoxRotation, value))
                    ApplyDefaultsToExistingProbes();
            }
        }

        protected internal override void OnBeginPlay()
        {
            base.OnBeginPlay();
            SpawnGrid();
        }

        protected internal override void OnEndPlay()
        {
            CleanupGrid();
            base.OnEndPlay();
        }

        protected override void OnDestroying()
        {
            CleanupGrid();
            base.OnDestroying();
        }

        private void SpawnGrid()
        {
            if (_spawnedNodes.Count > 0)
                return;

            // Ensure the parent has a default transform before spawning children
            _ = SceneNode.GetTransformAs<Transform>(true)!;

            var counts = ProbeCounts;
            Vector3 totalSize = new(
                (counts.X - 1) * Spacing.X,
                (counts.Y - 1) * Spacing.Y,
                (counts.Z - 1) * Spacing.Z);

            Vector3 start = -0.5f * totalSize + Offset;

            for (int x = 0; x < counts.X; ++x)
            {
                for (int y = 0; y < counts.Y; ++y)
                {
                    for (int z = 0; z < counts.Z; ++z)
                    {
                        Vector3 localPos = start + new Vector3(x * Spacing.X, y * Spacing.Y, z * Spacing.Z);
                        var child = new SceneNode(SceneNode, $"LightProbe[{x},{y},{z}]", new Transform(localPos));
                        var probe = child.AddComponent<LightProbeComponent>();
                        if (probe is not null)
                            ApplyDefaults(probe);
                        _spawnedNodes.Add(child);
                    }
                }
            }
        }

        private void RegenerateGridIfSpawned()
        {
            if (_spawnedNodes.Count == 0)
                return;

            CleanupGrid();
            SpawnGrid();
        }

        private void ApplyDefaultsToExistingProbes()
        {
            if (_spawnedNodes.Count == 0)
                return;

            foreach (var node in _spawnedNodes)
                if (node.TryGetComponent(out LightProbeComponent? probe) && probe is not null)
                    ApplyDefaults(probe);
        }

        private void ApplyDefaults(LightProbeComponent probe)
        {
            probe.RealtimeCapture = RealtimeCapture;
            probe.RealTimeCaptureUpdateInterval = RealTimeCaptureUpdateInterval;
            probe.IrradianceResolution = IrradianceResolution;
            probe.InfluenceShape = InfluenceShape;
            probe.InfluenceOffset = InfluenceOffset;
            probe.InfluenceSphereInnerRadius = InfluenceSphereInnerRadius;
            probe.InfluenceSphereOuterRadius = InfluenceSphereOuterRadius;
            probe.InfluenceBoxInnerExtents = InfluenceBoxInnerExtents;
            probe.InfluenceBoxOuterExtents = InfluenceBoxOuterExtents;
            probe.ParallaxCorrectionEnabled = ParallaxCorrectionEnabled;
            probe.ProxyBoxCenterOffset = ProxyBoxCenterOffset;
            probe.ProxyBoxHalfExtents = ProxyBoxHalfExtents;
            probe.ProxyBoxRotation = ProxyBoxRotation;
        }

        private void CleanupGrid()
        {
            if (_spawnedNodes.Count == 0)
                return;

            foreach (var node in _spawnedNodes)
                node.Destroy();

            _spawnedNodes.Clear();
        }
    }
}
