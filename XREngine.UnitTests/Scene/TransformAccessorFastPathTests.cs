using NUnit.Framework;
using Shouldly;
using System.Numerics;
using XREngine.Animation;
using XREngine.Components.Scene.Transforms;
using XREngine.Data.Components.Scene;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Scene;

[TestFixture]
public sealed class TransformAccessorFastPathTests
{
    [Test]
    public void LocalAccessors_UseTransformStateWithoutMatrixDecompose()
    {
        Transform transform = new()
        {
            ImmediateLocalMatrixRecalculation = false,
            Translation = new Vector3(3.0f, 4.0f, 5.0f),
            Rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.3f, -0.2f, 0.1f)),
        };

        transform.LocalTranslation.ShouldBe(new Vector3(3.0f, 4.0f, 5.0f));
        transform.LocalRotation.ShouldBe(transform.Rotation);
        transform.InverseLocalRotation.ShouldBe(Quaternion.Normalize(Quaternion.Inverse(transform.Rotation)));
        transform.IsLocalMatrixDirty.ShouldBeTrue();
    }

    [Test]
    public void WorldAccessors_ComposeHierarchyWithoutMatrixDecompose()
    {
        Transform parent = new()
        {
            Translation = new Vector3(10.0f, -2.0f, 7.0f),
            Rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.4f, 0.1f, -0.3f)),
        };
        parent.RecalculateMatrices(forceWorldRecalc: true);

        Transform child = new(parent)
        {
            Translation = new Vector3(1.0f, 2.0f, 3.0f),
            Rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(-0.2f, 0.5f, 0.25f)),
        };
        child.RecalculateMatrices(forceWorldRecalc: true);

        Vector3 expectedWorldTranslation = Vector3.Transform(child.Translation, parent.WorldMatrix);
        Quaternion expectedWorldRotation = Quaternion.Normalize(parent.WorldRotation * child.Rotation);
        Quaternion expectedInverseWorldRotation = Quaternion.Normalize(Quaternion.Inverse(child.Rotation) * parent.InverseWorldRotation);

        VectorShouldBeClose(child.WorldTranslation, expectedWorldTranslation);
        QuaternionShouldBeClose(child.WorldRotation, expectedWorldRotation);
        QuaternionShouldBeClose(child.InverseWorldRotation, expectedInverseWorldRotation);
    }

    [Test]
    public void RootRenderAccessors_UseRenderMatrixWhenRenderStateDiffersFromWorld()
    {
        Transform transform = new()
        {
            Translation = new Vector3(2.0f, 3.0f, 4.0f),
            Rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.1f, -0.2f, 0.3f)),
        };
        transform.RecalculateMatrices(forceWorldRecalc: true);

        Quaternion renderRotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(-0.35f, 0.25f, 0.4f));
        Matrix4x4 renderMatrix = Matrix4x4.CreateFromQuaternion(renderRotation) * Matrix4x4.CreateTranslation(new Vector3(-5.0f, 1.0f, 2.0f));

        transform.SetRenderMatrix(renderMatrix, recalcAllChildRenderMatrices: false).Wait();

        MatrixShouldBeClose(transform.InverseRenderMatrix * renderMatrix, Matrix4x4.Identity);
        VectorShouldBeClose(transform.RenderForward, Vector3.Normalize(Vector3.TransformNormal(Globals.Forward, renderMatrix)));
        VectorShouldBeClose(transform.RenderUp, Vector3.Normalize(Vector3.TransformNormal(Globals.Up, renderMatrix)));
        VectorShouldBeClose(transform.RenderRight, Vector3.Normalize(Vector3.TransformNormal(Globals.Right, renderMatrix)));
        QuaternionShouldBeClose(transform.RenderRotation, renderRotation);
        QuaternionShouldBeClose(transform.InverseRenderRotation, Quaternion.Normalize(Quaternion.Inverse(renderRotation)));
    }

    [Test]
    public void ChildRenderAccessors_ComposeParentRenderRotationWithoutWorldFallback()
    {
        Transform parent = new()
        {
            Translation = new Vector3(7.0f, -1.0f, 2.0f),
            Rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.2f, 0.15f, -0.1f)),
        };
        parent.RecalculateMatrices(forceWorldRecalc: true);

        Quaternion parentRenderRotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(-0.45f, 0.35f, 0.2f));
        Matrix4x4 parentRenderMatrix = Matrix4x4.CreateFromQuaternion(parentRenderRotation) * Matrix4x4.CreateTranslation(new Vector3(20.0f, -3.0f, 11.0f));
        parent.SetRenderMatrix(parentRenderMatrix, recalcAllChildRenderMatrices: false).Wait();

        Transform child = new(parent)
        {
            Translation = new Vector3(1.0f, 2.0f, 3.0f),
            Rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.05f, -0.25f, 0.3f)),
        };
        child.RecalculateMatrices(forceWorldRecalc: true);
        child.SetRenderMatrix(child.LocalMatrix * parent.RenderMatrix, recalcAllChildRenderMatrices: false).Wait();

        Quaternion expectedRenderRotation = Quaternion.Normalize(parentRenderRotation * child.Rotation);
        Quaternion expectedInverseRenderRotation = Quaternion.Normalize(Quaternion.Inverse(child.Rotation) * Quaternion.Normalize(Quaternion.Inverse(parentRenderRotation)));

        QuaternionShouldBeClose(child.RenderRotation, expectedRenderRotation);
        QuaternionShouldBeClose(child.InverseRenderRotation, expectedInverseRenderRotation);
    }

    [Test]
    public void MatrixBackedBaseAccessors_UseCachedLocalAndWorldOrientation()
    {
        Matrix4x4 localMatrix = Matrix4x4.CreateScale(new Vector3(1.5f, 0.75f, 2.0f))
            * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.35f, -0.25f, 0.15f)))
            * Matrix4x4.CreateTranslation(new Vector3(2.0f, -3.0f, 4.0f));

        MatrixBackedTransform root = new(localMatrix);
        root.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);

        Quaternion expectedRotation = Quaternion.Identity;
        Matrix4x4.Decompose(localMatrix, out _, out expectedRotation, out Vector3 expectedTranslation);
        expectedRotation = Quaternion.Normalize(expectedRotation);

        VectorShouldBeClose(root.LocalTranslation, expectedTranslation);
        QuaternionShouldBeClose(root.LocalRotation, expectedRotation);
        QuaternionShouldBeClose(root.InverseLocalRotation, Quaternion.Normalize(Quaternion.Inverse(expectedRotation)));
        VectorShouldBeClose(root.LocalForward, Vector3.Normalize(Vector3.TransformNormal(Globals.Forward, localMatrix)));
        VectorShouldBeClose(root.LocalUp, Vector3.Normalize(Vector3.TransformNormal(Globals.Up, localMatrix)));
        VectorShouldBeClose(root.LocalRight, Vector3.Normalize(Vector3.TransformNormal(Globals.Right, localMatrix)));

        VectorShouldBeClose(root.WorldTranslation, expectedTranslation);
        QuaternionShouldBeClose(root.WorldRotation, expectedRotation);
        QuaternionShouldBeClose(root.InverseWorldRotation, Quaternion.Normalize(Quaternion.Inverse(expectedRotation)));
        VectorShouldBeClose(root.WorldForward, Vector3.Normalize(Vector3.TransformNormal(Globals.Forward, localMatrix)));
        VectorShouldBeClose(root.WorldUp, Vector3.Normalize(Vector3.TransformNormal(Globals.Up, localMatrix)));
        VectorShouldBeClose(root.WorldRight, Vector3.Normalize(Vector3.TransformNormal(Globals.Right, localMatrix)));
    }

    [Test]
    public void AffineWorldComposition_MatchesMatrix4x4HierarchyComposition()
    {
        Transform root = new()
        {
            Scale = new Vector3(1.3f, 0.7f, 1.8f),
            Translation = new Vector3(5.0f, -2.0f, 9.0f),
            Rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.4f, -0.15f, 0.25f)),
        };
        root.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);

        Transform child = new(root)
        {
            Scale = new Vector3(0.8f, 1.4f, 1.1f),
            Translation = new Vector3(-3.0f, 1.0f, 2.0f),
            Rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(-0.2f, 0.3f, 0.1f)),
        };
        child.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);

        Matrix4x4 expectedWorldMatrix = child.LocalMatrix * root.WorldMatrix;

        MatrixShouldBeClose(child.WorldMatrix, expectedWorldMatrix);
        VectorShouldBeClose(child.WorldTranslation, expectedWorldMatrix.Translation);
    }

    [Test]
    public void RenderHierarchyPropagation_MatchesMatrix4x4Composition()
    {
        Transform parent = new()
        {
            Scale = new Vector3(1.2f, 0.9f, 1.1f),
            Translation = new Vector3(7.0f, -1.0f, 2.0f),
            Rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.2f, 0.15f, -0.1f)),
        };
        parent.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);

        Transform child = new(parent)
        {
            Scale = new Vector3(0.6f, 1.25f, 1.4f),
            Translation = new Vector3(1.0f, 2.0f, 3.0f),
            Rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.05f, -0.25f, 0.3f)),
        };
        child.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);

        Matrix4x4 parentRenderMatrix = Matrix4x4.CreateScale(new Vector3(1.1f, 0.8f, 1.3f))
            * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(-0.45f, 0.35f, 0.2f)))
            * Matrix4x4.CreateTranslation(new Vector3(20.0f, -3.0f, 11.0f));

        parent.SetRenderMatrix(parentRenderMatrix, recalcAllChildRenderMatrices: true).Wait();

        Matrix4x4 expectedChildRenderMatrix = child.LocalMatrix * parentRenderMatrix;
        MatrixShouldBeClose(child.RenderMatrix, expectedChildRenderMatrix);
    }

    [Test]
    public void NonAffineParentWorldComposition_FallsBackToMatrix4x4Path()
    {
        Matrix4x4 projectiveWorld = Matrix4x4.CreateTranslation(new Vector3(8.0f, -4.0f, 2.0f));
        projectiveWorld.M14 = 0.25f;

        DrivenWorldTransform parent = new(projectiveWorld);
        parent.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);

        Transform child = new(parent)
        {
            Scale = new Vector3(1.1f, 0.95f, 1.2f),
            Translation = new Vector3(1.0f, 2.0f, 3.0f),
            Rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(-0.2f, 0.5f, 0.25f)),
        };
        child.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);

        Matrix4x4 expectedWorldMatrix = child.LocalMatrix * parent.WorldMatrix;
        MatrixShouldBeClose(child.WorldMatrix, expectedWorldMatrix);
        child.WorldMatrix.M14.ShouldBe(expectedWorldMatrix.M14, 1e-4f);
    }

    [Test]
    public void NonAffineParentRenderPropagation_FallsBackToMatrix4x4Path()
    {
        Transform parent = new()
        {
            Translation = new Vector3(2.0f, -1.0f, 5.0f),
            Rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.1f, 0.2f, -0.15f)),
        };
        parent.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);

        Transform child = new(parent)
        {
            Scale = new Vector3(0.75f, 1.1f, 1.25f),
            Translation = new Vector3(4.0f, -2.0f, 1.5f),
            Rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.35f, -0.1f, 0.22f)),
        };
        child.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);

        Matrix4x4 nonAffineRenderMatrix = Matrix4x4.CreateTranslation(new Vector3(12.0f, -6.0f, 3.0f));
        nonAffineRenderMatrix.M24 = -0.4f;

        parent.SetRenderMatrix(nonAffineRenderMatrix, recalcAllChildRenderMatrices: true).Wait();

        Matrix4x4 expectedChildRenderMatrix = child.LocalMatrix * nonAffineRenderMatrix;
        MatrixShouldBeClose(child.RenderMatrix, expectedChildRenderMatrix);
        child.RenderMatrix.M24.ShouldBe(expectedChildRenderMatrix.M24, 1e-4f);
    }

    [TestCase(ETransformOrder.TRS)]
    [TestCase(ETransformOrder.STR)]
    [TestCase(ETransformOrder.RST)]
    [TestCase(ETransformOrder.RTS)]
    [TestCase(ETransformOrder.TSR)]
    [TestCase(ETransformOrder.SRT)]
    public void LocalMatrix_AllTransformOrders_MatchMatrix4x4Composition(ETransformOrder order)
    {
        Vector3 scale = new(1.3f, 0.7f, 1.8f);
        Vector3 translation = new(5.0f, -2.0f, 9.0f);
        Quaternion rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.4f, -0.15f, 0.25f));

        Transform transform = new(scale, translation, rotation, order: order);
        transform.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);

        MatrixShouldBeClose(transform.LocalMatrix, CreateExpectedLocalMatrix(scale, translation, rotation, order));
    }

    [Test]
    public void LocalMatrix_TrsRepresentativeSamples_MatchMatrix4x4Composition()
    {
        var random = new Random(20260319);

        for (int index = 0; index < 128; index++)
        {
            Vector3 scale = new(
                0.5f + (float)random.NextDouble() * 3.0f,
                0.5f + (float)random.NextDouble() * 3.0f,
                0.5f + (float)random.NextDouble() * 3.0f);
            Vector3 translation = new(
                -50.0f + (float)random.NextDouble() * 100.0f,
                -50.0f + (float)random.NextDouble() * 100.0f,
                -50.0f + (float)random.NextDouble() * 100.0f);
            Quaternion rotation = Quaternion.Normalize(new Quaternion(
                (float)random.NextDouble(),
                (float)random.NextDouble(),
                (float)random.NextDouble(),
                (float)random.NextDouble()));

            Transform transform = new(scale, translation, rotation, order: ETransformOrder.TRS);
            transform.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);

            MatrixShouldBeClose(transform.LocalMatrix, CreateExpectedLocalMatrix(scale, translation, rotation, ETransformOrder.TRS));
        }
    }

    [Test]
    public void LocalMatrix_RecomputeAfterOrderChange_MatchesExpectedMatrix()
    {
        Transform transform = new()
        {
            Scale = new Vector3(1.5f, 0.8f, 1.2f),
            Translation = new Vector3(-4.0f, 3.0f, 2.0f),
            Rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(-0.35f, 0.25f, 0.15f)),
            Order = ETransformOrder.TRS,
        };

        transform.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);
        Matrix4x4 trsMatrix = transform.LocalMatrix;

        transform.Order = ETransformOrder.SRT;
        transform.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);

        trsMatrix.ShouldNotBe(transform.LocalMatrix);
        MatrixShouldBeClose(transform.LocalMatrix, CreateExpectedLocalMatrix(transform.Scale, transform.Translation, transform.Rotation, ETransformOrder.SRT));
    }

    private static Matrix4x4 CreateExpectedLocalMatrix(Vector3 scale, Vector3 translation, Quaternion rotation, ETransformOrder order)
        => order switch
        {
            ETransformOrder.STR => Matrix4x4.CreateFromQuaternion(rotation)
                * Matrix4x4.CreateTranslation(translation)
                * Matrix4x4.CreateScale(scale),
            ETransformOrder.RST => Matrix4x4.CreateTranslation(translation)
                * Matrix4x4.CreateScale(scale)
                * Matrix4x4.CreateFromQuaternion(rotation),
            ETransformOrder.RTS => Matrix4x4.CreateScale(scale)
                * Matrix4x4.CreateTranslation(translation)
                * Matrix4x4.CreateFromQuaternion(rotation),
            ETransformOrder.TSR => Matrix4x4.CreateFromQuaternion(rotation)
                * Matrix4x4.CreateScale(scale)
                * Matrix4x4.CreateTranslation(translation),
            ETransformOrder.SRT => Matrix4x4.CreateTranslation(translation)
                * Matrix4x4.CreateFromQuaternion(rotation)
                * Matrix4x4.CreateScale(scale),
            _ => Matrix4x4.CreateScale(scale)
                * Matrix4x4.CreateFromQuaternion(rotation)
                * Matrix4x4.CreateTranslation(translation),
        };

    private static void VectorShouldBeClose(Vector3 actual, Vector3 expected)
    {
        actual.X.ShouldBe(expected.X, 1e-4f);
        actual.Y.ShouldBe(expected.Y, 1e-4f);
        actual.Z.ShouldBe(expected.Z, 1e-4f);
    }

    private static void QuaternionShouldBeClose(Quaternion actual, Quaternion expected)
    {
        actual.X.ShouldBe(expected.X, 1e-4f);
        actual.Y.ShouldBe(expected.Y, 1e-4f);
        actual.Z.ShouldBe(expected.Z, 1e-4f);
        actual.W.ShouldBe(expected.W, 1e-4f);
    }

    private static void MatrixShouldBeClose(Matrix4x4 actual, Matrix4x4 expected)
    {
        actual.M11.ShouldBe(expected.M11, 1e-4f);
        actual.M12.ShouldBe(expected.M12, 1e-4f);
        actual.M13.ShouldBe(expected.M13, 1e-4f);
        actual.M14.ShouldBe(expected.M14, 1e-4f);
        actual.M21.ShouldBe(expected.M21, 1e-4f);
        actual.M22.ShouldBe(expected.M22, 1e-4f);
        actual.M23.ShouldBe(expected.M23, 1e-4f);
        actual.M24.ShouldBe(expected.M24, 1e-4f);
        actual.M31.ShouldBe(expected.M31, 1e-4f);
        actual.M32.ShouldBe(expected.M32, 1e-4f);
        actual.M33.ShouldBe(expected.M33, 1e-4f);
        actual.M34.ShouldBe(expected.M34, 1e-4f);
        actual.M41.ShouldBe(expected.M41, 1e-4f);
        actual.M42.ShouldBe(expected.M42, 1e-4f);
        actual.M43.ShouldBe(expected.M43, 1e-4f);
        actual.M44.ShouldBe(expected.M44, 1e-4f);
    }

    [Test]
    public void LaggedTranslationAccessors_UseCurrentTranslationAndInheritedRotation()
    {
        Transform parent = new()
        {
            Translation = new Vector3(4.0f, -1.0f, 2.0f),
            Rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.25f, -0.1f, 0.4f)),
        };
        parent.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);

        LaggedTranslationTransform child = new(parent)
        {
            CurrentTranslation = new Vector3(1.0f, 0.5f, -2.0f),
        };
        child.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);

        child.LocalTranslation.ShouldBe(child.CurrentTranslation);
        child.LocalRotation.ShouldBe(Quaternion.Identity);
        VectorShouldBeClose(child.WorldTranslation, Vector3.Transform(child.CurrentTranslation, parent.WorldMatrix));
        QuaternionShouldBeClose(child.WorldRotation, parent.WorldRotation);
        QuaternionShouldBeClose(child.RenderRotation, parent.RenderRotation);
    }

    [Test]
    public void VrActionAccessors_ComposeFromDirectPositionAndRotation()
    {
        Transform parent = new()
        {
            Translation = new Vector3(6.0f, 2.0f, -3.0f),
            Rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(-0.15f, 0.3f, 0.2f)),
        };
        parent.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);

        TestVrActionTransform child = new(parent)
        {
            Position = new Vector3(-1.0f, 3.0f, 0.25f),
            Rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.45f, -0.2f, 0.05f)),
        };
        child.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);

        child.LocalTranslation.ShouldBe(child.Position);
        child.LocalRotation.ShouldBe(child.Rotation);
        VectorShouldBeClose(child.WorldTranslation, Vector3.Transform(child.Position, parent.WorldMatrix));
        QuaternionShouldBeClose(child.WorldRotation, Quaternion.Normalize(parent.WorldRotation * child.Rotation));
        QuaternionShouldBeClose(child.RenderRotation, Quaternion.Normalize(parent.RenderRotation * child.Rotation));
    }

    private sealed class TestVrActionTransform : VRActionTransformBase<TestActionCategory, TestActionName>
    {
        public TestVrActionTransform(TransformBase? parent)
            : base()
        {
            Parent = parent;
        }

        public new Vector3 Position
        {
            get => base.Position;
            set => base.Position = value;
        }

        public new Quaternion Rotation
        {
            get => base.Rotation;
            set => base.Rotation = value;
        }
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

    private enum TestActionCategory
    {
        Pose,
    }

    private enum TestActionName
    {
        Aim,
    }
}