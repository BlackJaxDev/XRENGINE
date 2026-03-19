using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using System.Numerics;
using XREngine.Data.Transforms;
using XREngine.Scene.Transforms;

[MemoryDiagnoser]
[Config(typeof(InProcessShortRunConfig))]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class AffineMatrixBenchmarks
{
    private Vector3[] _scales = null!;
    private Quaternion[] _rotations = null!;
    private Vector3[] _translations = null!;
    private Vector3[] _points = null!;
    private Matrix4x4[] _parents4x4 = null!;
    private AffineMatrix4x3[] _parentsAffine = null!;
    [Params(256, 4096)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(20260318);

        _scales = new Vector3[Count];
        _rotations = new Quaternion[Count];
        _translations = new Vector3[Count];
        _points = new Vector3[Count];
        _parents4x4 = new Matrix4x4[Count];
        _parentsAffine = new AffineMatrix4x3[Count];
        for (int index = 0; index < Count; index++)
        {
            Vector3 scale = new(
                0.5f + (float)random.NextDouble() * 2.5f,
                0.5f + (float)random.NextDouble() * 2.5f,
                0.5f + (float)random.NextDouble() * 2.5f);
            Quaternion rotation = Quaternion.Normalize(new Quaternion(
                (float)random.NextDouble(),
                (float)random.NextDouble(),
                (float)random.NextDouble(),
                (float)random.NextDouble()));
            Vector3 translation = new(
                -100.0f + (float)random.NextDouble() * 200.0f,
                -100.0f + (float)random.NextDouble() * 200.0f,
                -100.0f + (float)random.NextDouble() * 200.0f);
            Vector3 point = new(
                -5.0f + (float)random.NextDouble() * 10.0f,
                -5.0f + (float)random.NextDouble() * 10.0f,
                -5.0f + (float)random.NextDouble() * 10.0f);

            _scales[index] = scale;
            _rotations[index] = rotation;
            _translations[index] = translation;
            _points[index] = point;

            Matrix4x4 parent = Matrix4x4.CreateScale(
                    new Vector3(
                        0.75f + (float)random.NextDouble() * 2.0f,
                        0.75f + (float)random.NextDouble() * 2.0f,
                        0.75f + (float)random.NextDouble() * 2.0f))
                * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(new Quaternion(
                    (float)random.NextDouble(),
                    (float)random.NextDouble(),
                    (float)random.NextDouble(),
                    (float)random.NextDouble())))
                * Matrix4x4.CreateTranslation(new Vector3(
                    -50.0f + (float)random.NextDouble() * 100.0f,
                    -50.0f + (float)random.NextDouble() * 100.0f,
                    -50.0f + (float)random.NextDouble() * 100.0f));

            _parents4x4[index] = parent;
            _parentsAffine[index] = AffineMatrix4x3.FromMatrix4x4(parent);
        }
    }

    [Benchmark(Baseline = true)]
    public float Matrix4x4_CreateLocalTrs()
    {
        float accumulator = 0.0f;
        for (int index = 0; index < Count; index++)
        {
            Matrix4x4 local = Matrix4x4.CreateScale(_scales[index])
                * Matrix4x4.CreateFromQuaternion(_rotations[index])
                * Matrix4x4.CreateTranslation(_translations[index]);
            accumulator += local.M11 + local.M22 + local.M33 + local.M41;
        }

        return accumulator;
    }

    [Benchmark]
    public float Affine4x3_CreateLocalTrs()
    {
        float accumulator = 0.0f;
        for (int index = 0; index < Count; index++)
        {
            AffineMatrix4x3 local = AffineMatrix4x3.CreateTRS(_scales[index], _rotations[index], _translations[index]);
            accumulator += local.M11 + local.M22 + local.M33 + local.M41;
        }

        return accumulator;
    }

    [Benchmark]
    public float Matrix4x4_LocalToWorldMultiply()
    {
        float accumulator = 0.0f;
        for (int index = 0; index < Count; index++)
        {
            Matrix4x4 local = Matrix4x4.CreateScale(_scales[index])
                * Matrix4x4.CreateFromQuaternion(_rotations[index])
                * Matrix4x4.CreateTranslation(_translations[index]);
            Matrix4x4 world = local * _parents4x4[index];
            accumulator += world.M11 + world.M22 + world.M33 + world.M41;
        }

        return accumulator;
    }

    [Benchmark]
    public float Affine4x3_LocalToWorldMultiply()
    {
        float accumulator = 0.0f;
        for (int index = 0; index < Count; index++)
        {
            AffineMatrix4x3 local = AffineMatrix4x3.CreateTRS(_scales[index], _rotations[index], _translations[index]);
            AffineMatrix4x3 world = local * _parentsAffine[index];
            accumulator += world.M11 + world.M22 + world.M33 + world.M41;
        }

        return accumulator;
    }

    [Benchmark]
    public float Matrix4x4_TransformPoint()
    {
        float accumulator = 0.0f;
        for (int index = 0; index < Count; index++)
        {
            Matrix4x4 local = Matrix4x4.CreateScale(_scales[index])
                * Matrix4x4.CreateFromQuaternion(_rotations[index])
                * Matrix4x4.CreateTranslation(_translations[index]);
            Vector3 transformed = Vector3.Transform(_points[index], local);
            accumulator += transformed.X + transformed.Y + transformed.Z;
        }

        return accumulator;
    }

    [Benchmark]
    public float Affine4x3_TransformPoint()
    {
        float accumulator = 0.0f;
        for (int index = 0; index < Count; index++)
        {
            AffineMatrix4x3 local = AffineMatrix4x3.CreateTRS(_scales[index], _rotations[index], _translations[index]);
            Vector3 transformed = local.TransformPosition(_points[index]);
            accumulator += transformed.X + transformed.Y + transformed.Z;
        }

        return accumulator;
    }
}

[MemoryDiagnoser]
[Config(typeof(InProcessShortRunConfig))]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class AffineMatrixScenarioBenchmarks
{
    private static readonly Vector3[] CornerSigns =
    [
        new(-1.0f, -1.0f, -1.0f),
        new(-1.0f, -1.0f, 1.0f),
        new(-1.0f, 1.0f, -1.0f),
        new(-1.0f, 1.0f, 1.0f),
        new(1.0f, -1.0f, -1.0f),
        new(1.0f, -1.0f, 1.0f),
        new(1.0f, 1.0f, -1.0f),
        new(1.0f, 1.0f, 1.0f),
    ];

    private Matrix4x4[] _chainLocals4x4 = null!;
    private AffineMatrix4x3[] _chainLocalsAffine = null!;
    private Matrix4x4[] _chainWorlds4x4 = null!;
    private AffineMatrix4x3[] _chainWorldsAffine = null!;
    private Matrix4x4[] _wideChildLocals4x4 = null!;
    private AffineMatrix4x3[] _wideChildLocalsAffine = null!;
    private Matrix4x4[] _wideChildRenders4x4 = null!;
    private AffineMatrix4x3[] _wideChildRendersAffine = null!;
    private Matrix4x4[] _boundsMatrices4x4 = null!;
    private AffineMatrix4x3[] _boundsMatricesAffine = null!;
    private Vector3[] _boundsCenters = null!;
    private Vector3[] _boundsExtents = null!;
    private Matrix4x4 _parentRender4x4;
    private AffineMatrix4x3 _parentRenderAffine;

    [Params(64, 512)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(20260318);

        _chainLocals4x4 = new Matrix4x4[Count];
        _chainLocalsAffine = new AffineMatrix4x3[Count];
        _chainWorlds4x4 = new Matrix4x4[Count];
        _chainWorldsAffine = new AffineMatrix4x3[Count];
        _wideChildLocals4x4 = new Matrix4x4[Count];
        _wideChildLocalsAffine = new AffineMatrix4x3[Count];
        _wideChildRenders4x4 = new Matrix4x4[Count];
        _wideChildRendersAffine = new AffineMatrix4x3[Count];
        _boundsMatrices4x4 = new Matrix4x4[Count];
        _boundsMatricesAffine = new AffineMatrix4x3[Count];
        _boundsCenters = new Vector3[Count];
        _boundsExtents = new Vector3[Count];

        _parentRenderAffine = CreateRandomAffine(random);
        _parentRender4x4 = _parentRenderAffine.ToMatrix4x4();

        for (int index = 0; index < Count; index++)
        {
            AffineMatrix4x3 chainLocal = CreateRandomAffine(random);
            _chainLocalsAffine[index] = chainLocal;
            _chainLocals4x4[index] = chainLocal.ToMatrix4x4();

            AffineMatrix4x3 wideChildLocal = CreateRandomAffine(random);
            _wideChildLocalsAffine[index] = wideChildLocal;
            _wideChildLocals4x4[index] = wideChildLocal.ToMatrix4x4();

            AffineMatrix4x3 boundsMatrix = CreateRandomAffine(random);
            _boundsMatricesAffine[index] = boundsMatrix;
            _boundsMatrices4x4[index] = boundsMatrix.ToMatrix4x4();
            _boundsCenters[index] = new Vector3(
                -5.0f + (float)random.NextDouble() * 10.0f,
                -5.0f + (float)random.NextDouble() * 10.0f,
                -5.0f + (float)random.NextDouble() * 10.0f);
            _boundsExtents[index] = new Vector3(
                0.25f + (float)random.NextDouble() * 4.0f,
                0.25f + (float)random.NextDouble() * 4.0f,
                0.25f + (float)random.NextDouble() * 4.0f);
        }
    }

    [Benchmark(Baseline = true)]
    public float Matrix4x4_DirtyTransformChainUpdate()
    {
        float accumulator = 0.0f;
        Matrix4x4 parentWorld = Matrix4x4.Identity;
        for (int index = 0; index < Count; index++)
        {
            Matrix4x4 world = _chainLocals4x4[index] * parentWorld;
            _chainWorlds4x4[index] = world;
            parentWorld = world;
            accumulator += world.M11 + world.M22 + world.M33 + world.M41 + world.M42 + world.M43;
        }

        return accumulator;
    }

    [Benchmark]
    public float Affine4x3_DirtyTransformChainUpdate()
    {
        float accumulator = 0.0f;
        AffineMatrix4x3 parentWorld = AffineMatrix4x3.Identity;
        for (int index = 0; index < Count; index++)
        {
            AffineMatrix4x3 world = _chainLocalsAffine[index] * parentWorld;
            _chainWorldsAffine[index] = world;
            parentWorld = world;
            accumulator += world.M11 + world.M22 + world.M33 + world.M41 + world.M42 + world.M43;
        }

        return accumulator;
    }

    [Benchmark]
    public float Matrix4x4_WideRenderPropagation()
    {
        float accumulator = 0.0f;
        for (int index = 0; index < Count; index++)
        {
            Matrix4x4 render = _wideChildLocals4x4[index] * _parentRender4x4;
            _wideChildRenders4x4[index] = render;
            accumulator += render.M11 + render.M22 + render.M33 + render.M41 + render.M42 + render.M43;
        }

        return accumulator;
    }

    [Benchmark]
    public float Affine4x3_WideRenderPropagation()
    {
        float accumulator = 0.0f;
        for (int index = 0; index < Count; index++)
        {
            AffineMatrix4x3 render = _wideChildLocalsAffine[index] * _parentRenderAffine;
            _wideChildRendersAffine[index] = render;
            accumulator += render.M11 + render.M22 + render.M33 + render.M41 + render.M42 + render.M43;
        }

        return accumulator;
    }

    [Benchmark]
    public float Matrix4x4_CpuBoundsUpdate()
    {
        float accumulator = 0.0f;
        for (int index = 0; index < Count; index++)
        {
            Matrix4x4 matrix = _boundsMatrices4x4[index];
            Vector3 center = _boundsCenters[index];
            Vector3 extent = _boundsExtents[index];
            Vector3 worldCenter = Vector3.Transform(center, matrix);
            Vector3 min = new(float.PositiveInfinity);
            Vector3 max = new(float.NegativeInfinity);

            for (int cornerIndex = 0; cornerIndex < CornerSigns.Length; cornerIndex++)
            {
                Vector3 corner = center + extent * CornerSigns[cornerIndex];
                Vector3 transformed = Vector3.Transform(corner, matrix);
                min = Vector3.Min(min, transformed);
                max = Vector3.Max(max, transformed);
            }

            accumulator += worldCenter.X + worldCenter.Y + worldCenter.Z;
            accumulator += max.X - min.X + max.Y - min.Y + max.Z - min.Z;
        }

        return accumulator;
    }

    [Benchmark]
    public float Affine4x3_CpuBoundsUpdate()
    {
        float accumulator = 0.0f;
        for (int index = 0; index < Count; index++)
        {
            AffineMatrix4x3 matrix = _boundsMatricesAffine[index];
            Vector3 center = _boundsCenters[index];
            Vector3 extent = _boundsExtents[index];
            Vector3 worldCenter = matrix.TransformPosition(center);
            Vector3 min = new(float.PositiveInfinity);
            Vector3 max = new(float.NegativeInfinity);

            for (int cornerIndex = 0; cornerIndex < CornerSigns.Length; cornerIndex++)
            {
                Vector3 corner = center + extent * CornerSigns[cornerIndex];
                Vector3 transformed = matrix.TransformPosition(corner);
                min = Vector3.Min(min, transformed);
                max = Vector3.Max(max, transformed);
            }

            accumulator += worldCenter.X + worldCenter.Y + worldCenter.Z;
            accumulator += max.X - min.X + max.Y - min.Y + max.Z - min.Z;
        }

        return accumulator;
    }

    private static AffineMatrix4x3 CreateRandomAffine(Random random)
    {
        Vector3 scale = new(
            0.25f + (float)random.NextDouble() * 3.0f,
            0.25f + (float)random.NextDouble() * 3.0f,
            0.25f + (float)random.NextDouble() * 3.0f);
        Quaternion rotation = Quaternion.Normalize(new Quaternion(
            (float)random.NextDouble(),
            (float)random.NextDouble(),
            (float)random.NextDouble(),
            (float)random.NextDouble()));
        Vector3 translation = new(
            -100.0f + (float)random.NextDouble() * 200.0f,
            -100.0f + (float)random.NextDouble() * 200.0f,
            -100.0f + (float)random.NextDouble() * 200.0f);

        return AffineMatrix4x3.CreateTRS(scale, rotation, translation);
    }
}

[MemoryDiagnoser]
[Config(typeof(InProcessMediumRunConfig))]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class TransformHierarchyBenchmarks
{
    private BenchmarkTransform[] _affineChain = null!;
    private BenchmarkTransform[] _matrixChain = null!;
    private BenchmarkTransform _affineRenderParent = null!;
    private BenchmarkTransform _matrixRenderParent = null!;
    private BenchmarkTransform[] _affineRenderChildren = null!;
    private BenchmarkTransform[] _matrixRenderChildren = null!;
    private Matrix4x4 _parentRenderMatrix;

    [Params(64, 512)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(20260318);

        _affineChain = new BenchmarkTransform[Count];
        _matrixChain = new BenchmarkTransform[Count];
        BenchmarkTransform? affineParent = null;
        BenchmarkTransform? matrixParent = null;

        for (int index = 0; index < Count; index++)
        {
            Matrix4x4 localMatrix = CreateRandomAffineMatrix(random);

            BenchmarkTransform affineTransform = new(localMatrix, isGuaranteedAffine: true, affineParent);
            affineTransform.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: false);
            _affineChain[index] = affineTransform;
            affineParent = affineTransform;

            BenchmarkTransform matrixTransform = new(localMatrix, isGuaranteedAffine: false, matrixParent);
            matrixTransform.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: false);
            _matrixChain[index] = matrixTransform;
            matrixParent = matrixTransform;
        }

        _parentRenderMatrix = CreateRandomAffineMatrix(random);
        _affineRenderParent = new(CreateRandomAffineMatrix(random), isGuaranteedAffine: true, parent: null);
        _matrixRenderParent = new(CreateRandomAffineMatrix(random), isGuaranteedAffine: false, parent: null);
        _affineRenderParent.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);
        _matrixRenderParent.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);

        _affineRenderChildren = new BenchmarkTransform[Count];
        _matrixRenderChildren = new BenchmarkTransform[Count];
        for (int index = 0; index < Count; index++)
        {
            Matrix4x4 localMatrix = CreateRandomAffineMatrix(random);

            BenchmarkTransform affineChild = new(localMatrix, isGuaranteedAffine: true, _affineRenderParent);
            affineChild.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);
            _affineRenderChildren[index] = affineChild;

            BenchmarkTransform matrixChild = new(localMatrix, isGuaranteedAffine: false, _matrixRenderParent);
            matrixChild.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);
            _matrixRenderChildren[index] = matrixChild;
        }
    }

    [Benchmark(Baseline = true)]
    public float MatrixBacked_DirtyHierarchyRecalc()
    {
        float accumulator = 0.0f;
        for (int index = 0; index < Count; index++)
        {
            BenchmarkTransform transform = _matrixChain[index];
            transform.RecalcWorld();
            Matrix4x4 world = transform.WorldMatrix;
            accumulator += world.M11 + world.M22 + world.M33 + world.M41 + world.M42 + world.M43;
        }

        return accumulator;
    }

    [Benchmark]
    public float AffineEligible_DirtyHierarchyRecalc()
    {
        float accumulator = 0.0f;
        for (int index = 0; index < Count; index++)
        {
            BenchmarkTransform transform = _affineChain[index];
            transform.RecalcWorld();
            Matrix4x4 world = transform.WorldMatrix;
            accumulator += world.M11 + world.M22 + world.M33 + world.M41 + world.M42 + world.M43;
        }

        return accumulator;
    }

    [Benchmark]
    public float MatrixBacked_RenderHierarchyPropagation()
    {
        _matrixRenderParent.SetRenderMatrix(_parentRenderMatrix, recalcAllChildRenderMatrices: true).Wait();

        float accumulator = 0.0f;
        for (int index = 0; index < Count; index++)
        {
            Matrix4x4 render = _matrixRenderChildren[index].RenderMatrix;
            accumulator += render.M11 + render.M22 + render.M33 + render.M41 + render.M42 + render.M43;
        }

        return accumulator;
    }

    [Benchmark]
    public float AffineEligible_RenderHierarchyPropagation()
    {
        _affineRenderParent.SetRenderMatrix(_parentRenderMatrix, recalcAllChildRenderMatrices: true).Wait();

        float accumulator = 0.0f;
        for (int index = 0; index < Count; index++)
        {
            Matrix4x4 render = _affineRenderChildren[index].RenderMatrix;
            accumulator += render.M11 + render.M22 + render.M33 + render.M41 + render.M42 + render.M43;
        }

        return accumulator;
    }

    private static Matrix4x4 CreateRandomAffineMatrix(Random random)
        => AffineMatrixScenarioBenchmarks_CreateRandomAffine(random).ToMatrix4x4();

    private static AffineMatrix4x3 AffineMatrixScenarioBenchmarks_CreateRandomAffine(Random random)
    {
        Vector3 scale = new(
            0.25f + (float)random.NextDouble() * 3.0f,
            0.25f + (float)random.NextDouble() * 3.0f,
            0.25f + (float)random.NextDouble() * 3.0f);
        Quaternion rotation = Quaternion.Normalize(new Quaternion(
            (float)random.NextDouble(),
            (float)random.NextDouble(),
            (float)random.NextDouble(),
            (float)random.NextDouble()));
        Vector3 translation = new(
            -100.0f + (float)random.NextDouble() * 200.0f,
            -100.0f + (float)random.NextDouble() * 200.0f,
            -100.0f + (float)random.NextDouble() * 200.0f);

        return AffineMatrix4x3.CreateTRS(scale, rotation, translation);
    }

    private sealed class BenchmarkTransform : TransformBase
    {
        private readonly Matrix4x4 _localMatrix;
        private readonly AffineMatrix4x3 _localAffineMatrix;
        private readonly bool _isGuaranteedAffine;

        public BenchmarkTransform(Matrix4x4 localMatrix, bool isGuaranteedAffine, TransformBase? parent)
            : base(parent)
        {
            _localMatrix = localMatrix;
            _localAffineMatrix = AffineMatrix4x3.FromMatrix4x4(localMatrix);
            _isGuaranteedAffine = isGuaranteedAffine;
            MarkLocalModified();
        }

        protected override bool IsGuaranteedAffine => _isGuaranteedAffine;

        protected override bool TryGetLocalAffineMatrix(out AffineMatrix4x3 matrix)
        {
            if (!_isGuaranteedAffine)
            {
                matrix = default;
                return false;
            }

            matrix = _localAffineMatrix;
            return true;
        }

        protected override Matrix4x4 CreateLocalMatrix()
            => _localMatrix;
    }
}

[MemoryDiagnoser]
[Config(typeof(InProcessShortRunConfig))]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class TransformAccessorBenchmarks
{
    private Transform[] _transforms = null!;
    private MatrixBackedTransform[] _matrixBackedTransforms = null!;
    private MatrixBackedTransform[] _snapshotEnabledTransforms = null!;
    private float _accumulator;

    [Params(256, 4096)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(20260318);
        _transforms = new Transform[Count];
        _matrixBackedTransforms = new MatrixBackedTransform[Count];
        _snapshotEnabledTransforms = new MatrixBackedTransform[Count];

        for (int index = 0; index < Count; index++)
        {
            Quaternion localRotation = Quaternion.Normalize(new Quaternion(
                (float)random.NextDouble(),
                (float)random.NextDouble(),
                (float)random.NextDouble(),
                (float)random.NextDouble()));
            Vector3 localTranslation = new(
                -100.0f + (float)random.NextDouble() * 200.0f,
                -100.0f + (float)random.NextDouble() * 200.0f,
                -100.0f + (float)random.NextDouble() * 200.0f);

            Transform transform = new()
            {
                Translation = localTranslation,
                Rotation = localRotation,
                Scale = Vector3.One,
            };
            transform.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);

            Quaternion renderRotation = Quaternion.Normalize(new Quaternion(
                (float)random.NextDouble(),
                (float)random.NextDouble(),
                (float)random.NextDouble(),
                (float)random.NextDouble()));
            Vector3 renderTranslation = new(
                -100.0f + (float)random.NextDouble() * 200.0f,
                -100.0f + (float)random.NextDouble() * 200.0f,
                -100.0f + (float)random.NextDouble() * 200.0f);
            Matrix4x4 renderMatrix = Matrix4x4.CreateFromQuaternion(renderRotation) * Matrix4x4.CreateTranslation(renderTranslation);
            transform.SetRenderMatrix(renderMatrix, recalcAllChildRenderMatrices: false).Wait();

            _transforms[index] = transform;

            Matrix4x4 localMatrix = Matrix4x4.CreateScale(new Vector3(
                    0.5f + (float)random.NextDouble() * 2.0f,
                    0.5f + (float)random.NextDouble() * 2.0f,
                    0.5f + (float)random.NextDouble() * 2.0f))
                * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(new Quaternion(
                    (float)random.NextDouble(),
                    (float)random.NextDouble(),
                    (float)random.NextDouble(),
                    (float)random.NextDouble())))
                * Matrix4x4.CreateTranslation(new Vector3(
                    -25.0f + (float)random.NextDouble() * 50.0f,
                    -25.0f + (float)random.NextDouble() * 50.0f,
                    -25.0f + (float)random.NextDouble() * 50.0f));
            MatrixBackedTransform matrixBackedTransform = new(localMatrix);
            matrixBackedTransform.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);
            _matrixBackedTransforms[index] = matrixBackedTransform;

            // Separate snapshot-enabled instance for the bulk read benchmark
            MatrixBackedTransform snapshotTransform = new(localMatrix);
            snapshotTransform.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);
            // Pre-enable snapshots so UpdateCache populates them on subsequent recalcs
            snapshotTransform.GetLocalSnapshot();
            snapshotTransform.GetWorldSnapshot();
            _snapshotEnabledTransforms[index] = snapshotTransform;
        }
    }

    [Benchmark(Baseline = true)]
    public float MatrixDecompose_AccessorBaseline()
    {
        float accumulator = _accumulator;
        for (int index = 0; index < Count; index++)
        {
            Transform transform = _transforms[index];

            Matrix4x4.Decompose(transform.LocalMatrix, out _, out Quaternion localRotation, out Vector3 localTranslation);
            Matrix4x4.Decompose(transform.InverseLocalMatrix, out _, out Quaternion inverseLocalRotation, out _);
            Matrix4x4.Decompose(transform.WorldMatrix, out _, out Quaternion worldRotation, out Vector3 worldTranslation);
            Matrix4x4.Decompose(transform.InverseWorldMatrix, out _, out Quaternion inverseWorldRotation, out _);
            Matrix4x4.Decompose(transform.RenderMatrix, out _, out Quaternion renderRotation, out Vector3 renderTranslation);
            Matrix4x4.Decompose(transform.InverseRenderMatrix, out _, out Quaternion inverseRenderRotation, out _);

            accumulator += localTranslation.X + worldTranslation.Y + renderTranslation.Z;
            accumulator += localRotation.W + inverseLocalRotation.W + worldRotation.W + inverseWorldRotation.W;
            accumulator += renderRotation.W + inverseRenderRotation.W;
        }

        _accumulator = accumulator;
        return accumulator;
    }

    [Benchmark]
    public float Transform_FastPathAccessorReads()
    {
        float accumulator = _accumulator;
        for (int index = 0; index < Count; index++)
        {
            Transform transform = _transforms[index];
            Vector3 localTranslation = transform.LocalTranslation;
            Quaternion localRotation = transform.LocalRotation;
            Quaternion inverseLocalRotation = transform.InverseLocalRotation;
            Vector3 worldTranslation = transform.WorldTranslation;
            Quaternion worldRotation = transform.WorldRotation;
            Quaternion inverseWorldRotation = transform.InverseWorldRotation;
            Vector3 renderTranslation = transform.RenderTranslation;
            Quaternion renderRotation = transform.RenderRotation;
            Quaternion inverseRenderRotation = transform.InverseRenderRotation;

            accumulator += localTranslation.X + worldTranslation.Y + renderTranslation.Z;
            accumulator += localRotation.W + inverseLocalRotation.W + worldRotation.W + inverseWorldRotation.W;
            accumulator += renderRotation.W + inverseRenderRotation.W;
        }

        _accumulator = accumulator;
        return accumulator;
    }

    [Benchmark]
    public float MatrixBackedTransform_DecomposeBaseline()
    {
        float accumulator = _accumulator;
        for (int index = 0; index < Count; index++)
        {
            MatrixBackedTransform transform = _matrixBackedTransforms[index];

            Matrix4x4 localMatrix = transform.LocalMatrix;
            Matrix4x4 worldMatrix = transform.WorldMatrix;
            Matrix4x4 inverseLocalMatrix = transform.InverseLocalMatrix;
            Matrix4x4 inverseWorldMatrix = transform.InverseWorldMatrix;

            Matrix4x4.Decompose(localMatrix, out _, out Quaternion localRotation, out Vector3 localTranslation);
            Matrix4x4.Decompose(worldMatrix, out _, out Quaternion worldRotation, out Vector3 worldTranslation);
            Matrix4x4.Decompose(inverseLocalMatrix, out _, out Quaternion inverseLocalRotation, out _);
            Matrix4x4.Decompose(inverseWorldMatrix, out _, out Quaternion inverseWorldRotation, out _);
            Vector3 localForward = Vector3.Normalize(Vector3.TransformNormal(XREngine.Globals.Forward, localMatrix));
            Vector3 localUp = Vector3.Normalize(Vector3.TransformNormal(XREngine.Globals.Up, localMatrix));
            Vector3 localRight = Vector3.Normalize(Vector3.TransformNormal(XREngine.Globals.Right, localMatrix));
            Vector3 worldForward = Vector3.Normalize(Vector3.TransformNormal(XREngine.Globals.Forward, worldMatrix));
            Vector3 worldUp = Vector3.Normalize(Vector3.TransformNormal(XREngine.Globals.Up, worldMatrix));
            Vector3 worldRight = Vector3.Normalize(Vector3.TransformNormal(XREngine.Globals.Right, worldMatrix));

            accumulator += localTranslation.X + worldTranslation.Y;
            accumulator += localRotation.W + inverseLocalRotation.W + worldRotation.W + inverseWorldRotation.W;
            accumulator += localForward.Z + localUp.Y + localRight.X;
            accumulator += worldForward.Z + worldUp.Y + worldRight.X;
        }

        _accumulator = accumulator;
        return accumulator;
    }

    [Benchmark]
    public float MatrixBackedTransform_OnTheFlyAccessors()
    {
        float accumulator = _accumulator;
        for (int index = 0; index < Count; index++)
        {
            MatrixBackedTransform transform = _matrixBackedTransforms[index];

            Vector3 localTranslation = transform.LocalTranslation;
            Quaternion localRotation = transform.LocalRotation;
            Quaternion inverseLocalRotation = transform.InverseLocalRotation;
            Vector3 localForward = transform.LocalForward;
            Vector3 localUp = transform.LocalUp;
            Vector3 localRight = transform.LocalRight;
            Vector3 worldTranslation = transform.WorldTranslation;
            Quaternion worldRotation = transform.WorldRotation;
            Quaternion inverseWorldRotation = transform.InverseWorldRotation;
            Vector3 worldForward = transform.WorldForward;
            Vector3 worldUp = transform.WorldUp;
            Vector3 worldRight = transform.WorldRight;

            accumulator += localTranslation.X + worldTranslation.Y;
            accumulator += localRotation.W + inverseLocalRotation.W + worldRotation.W + inverseWorldRotation.W;
            accumulator += localForward.Z + localUp.Y + localRight.X;
            accumulator += worldForward.Z + worldUp.Y + worldRight.X;
        }

        _accumulator = accumulator;
        return accumulator;
    }

    [Benchmark]
    public float MatrixBackedTransform_BulkSnapshotReads()
    {
        float accumulator = _accumulator;
        for (int index = 0; index < Count; index++)
        {
            MatrixBackedTransform transform = _snapshotEnabledTransforms[index];

            // 2 lock acquisitions total (one local, one world) instead of 12
            TransformBase.SpaceSnapshot local = transform.GetLocalSnapshot();
            TransformBase.SpaceSnapshot world = transform.GetWorldSnapshot();

            Vector3 localTranslation = transform.LocalTranslation;
            Vector3 worldTranslation = transform.WorldTranslation;

            accumulator += localTranslation.X + worldTranslation.Y;
            accumulator += local.Rotation.W + local.InverseRotation.W + world.Rotation.W + world.InverseRotation.W;
            accumulator += local.Forward.Z + local.Up.Y + local.Right.X;
            accumulator += world.Forward.Z + world.Up.Y + world.Right.X;
        }

        _accumulator = accumulator;
        return accumulator;
    }

    private sealed class MatrixBackedTransform : TransformBase
    {
        private readonly Matrix4x4 _localMatrix;

        public MatrixBackedTransform(Matrix4x4 localMatrix)
            : base(null)
        {
            _localMatrix = localMatrix;
            MarkLocalModified();
        }

        protected override Matrix4x4 CreateLocalMatrix()
            => _localMatrix;
    }
}

public sealed class InProcessShortRunConfig : ManualConfig
{
    public InProcessShortRunConfig()
        => AddJob(Job.ShortRun.WithToolchain(InProcessNoEmitToolchain.Instance));
}

public sealed class InProcessMediumRunConfig : ManualConfig
{
    public InProcessMediumRunConfig()
        => AddJob(Job.MediumRun.WithToolchain(InProcessNoEmitToolchain.Instance));
}