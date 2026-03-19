using NUnit.Framework;
using System.Numerics;
using XREngine.Data.Core;
using Assert = NUnit.Framework.Assert;

namespace XREngine.UnitTests;

[TestFixture]
public class ProjectionMatrixCombinerTests
{
    private const float Near = 0.3f;
    private const float Far = 20.0f;
    private const float Aspect = 16.0f / 9.0f;

    [Test]
    public void CombineProjectionMatrices_StereoPair_EnclosesSourceFrusta()
    {
        Matrix4x4 leftProjection = CreatePerspective(65.0f);
        Matrix4x4 rightProjection = CreatePerspective(65.0f);
        Matrix4x4 leftView = Matrix4x4.CreateTranslation(0.032f, 0.0f, 0.0f);
        Matrix4x4 rightView = Matrix4x4.CreateTranslation(-0.032f, 0.0f, 0.0f);

        Matrix4x4 combined = ProjectionMatrixCombiner.CombineProjectionMatrices(
            leftProjection,
            rightProjection,
            leftView,
            rightView);

        AssertFrustumContained(combined, GetFrustumCorners(leftProjection, leftView));
        AssertFrustumContained(combined, GetFrustumCorners(rightProjection, rightView));
    }

    [Test]
    public void CombineProjectionMatrices_ThreeFrusta_EnclosesAllSourceFrusta()
    {
        Matrix4x4 leftProjection = CreatePerspective(65.0f);
        Matrix4x4 rightProjection = CreatePerspective(65.0f);
        Matrix4x4 cyclopsProjection = CreatePerspective(45.0f);

        Quaternion cyclopsRotation =
            Quaternion.CreateFromAxisAngle(Globals.Up, XRMath.DegToRad(20.0f)) *
            Quaternion.CreateFromAxisAngle(Globals.Right, XRMath.DegToRad(10.0f));
        Matrix4x4 cyclopsWorld = Matrix4x4.CreateFromQuaternion(cyclopsRotation);

        Matrix4x4 leftView = cyclopsWorld * Matrix4x4.CreateTranslation(0.032f, 0.0f, 0.0f);
        Matrix4x4 rightView = cyclopsWorld * Matrix4x4.CreateTranslation(-0.032f, 0.0f, 0.0f);

        Matrix4x4 combined = ProjectionMatrixCombiner.CombineProjectionMatrices(
            [leftProjection, rightProjection, cyclopsProjection],
            [leftView, rightView, Matrix4x4.Identity]);

        AssertFrustumContained(combined, GetFrustumCorners(leftProjection, leftView));
        AssertFrustumContained(combined, GetFrustumCorners(rightProjection, rightView));
        AssertFrustumContained(combined, GetFrustumCorners(cyclopsProjection, Matrix4x4.Identity));
    }

    [Test]
    public void CombineProjectionMatrices_CanPreferStereoSourcesForFarDistance()
    {
        Matrix4x4 leftProjection = CreatePerspective(65.0f);
        Matrix4x4 rightProjection = CreatePerspective(65.0f);
        Matrix4x4 cyclopsProjection = CreatePerspective(45.0f);

        Quaternion cyclopsRotation = Quaternion.CreateFromAxisAngle(Globals.Up, XRMath.DegToRad(25.0f));
        Matrix4x4 cyclopsView = Matrix4x4.CreateFromQuaternion(cyclopsRotation);

        Matrix4x4 combinedAllSources = ProjectionMatrixCombiner.CombineProjectionMatrices(
            [leftProjection, rightProjection, cyclopsProjection],
            [Matrix4x4.Identity, Matrix4x4.Identity, cyclopsView]);

        Matrix4x4 combinedStereoFar = ProjectionMatrixCombiner.CombineProjectionMatrices(
            [leftProjection, rightProjection, cyclopsProjection],
            [Matrix4x4.Identity, Matrix4x4.Identity, cyclopsView],
            farBoundsSourceCount: 2);

        AssertFrustumContained(combinedStereoFar, GetFrustumCorners(leftProjection, Matrix4x4.Identity));
        AssertFrustumContained(combinedStereoFar, GetFrustumCorners(rightProjection, Matrix4x4.Identity));
        Assert.That(GetFarDistance(combinedStereoFar), Is.LessThan(GetFarDistance(combinedAllSources)));
    }

    [Test]
    public void SolveMinimalEnclosingFrustum_CanImproveCombinedViewOrientation()
    {
        Matrix4x4 leftProjection = CreatePerspective(65.0f);
        Matrix4x4 rightProjection = CreatePerspective(65.0f);
        Matrix4x4 cyclopsProjection = CreatePerspective(45.0f);

        Quaternion cyclopsRotation =
            Quaternion.CreateFromAxisAngle(Globals.Up, XRMath.DegToRad(32.0f)) *
            Quaternion.CreateFromAxisAngle(Globals.Right, XRMath.DegToRad(9.0f));
        Matrix4x4 cyclopsView = Matrix4x4.CreateFromQuaternion(cyclopsRotation);

        ProjectionMatrixCombiner.FrustumSolveResult fixedView = ProjectionMatrixCombiner.SolveMinimalEnclosingFrustum(
            [leftProjection, rightProjection, cyclopsProjection],
            [Matrix4x4.CreateTranslation(0.032f, 0.0f, 0.0f), Matrix4x4.CreateTranslation(-0.032f, 0.0f, 0.0f), cyclopsView],
            farBoundsSourceCount: null,
            solveViewOrientation: false);

        ProjectionMatrixCombiner.FrustumSolveResult solvedView = ProjectionMatrixCombiner.SolveMinimalEnclosingFrustum(
            [leftProjection, rightProjection, cyclopsProjection],
            [Matrix4x4.CreateTranslation(0.032f, 0.0f, 0.0f), Matrix4x4.CreateTranslation(-0.032f, 0.0f, 0.0f), cyclopsView],
            farBoundsSourceCount: null,
            solveViewOrientation: true);

        AssertFrustumContained(solvedView.View, solvedView.Projection, GetFrustumCorners(leftProjection, Matrix4x4.CreateTranslation(0.032f, 0.0f, 0.0f)));
        AssertFrustumContained(solvedView.View, solvedView.Projection, GetFrustumCorners(rightProjection, Matrix4x4.CreateTranslation(-0.032f, 0.0f, 0.0f)));
        AssertFrustumContained(solvedView.View, solvedView.Projection, GetFrustumCorners(cyclopsProjection, cyclopsView));
        Assert.That(string.IsNullOrWhiteSpace(solvedView.CandidateLabel), Is.False);
        Assert.That(GetProjectionCost(solvedView.Projection), Is.LessThanOrEqualTo(GetProjectionCost(fixedView.Projection) + 1e-5f));
    }

    [Test]
    public void SolveMinimalEnclosingFrustum_HighSpeedMode_MatchesNonRefinedSolve()
    {
        Matrix4x4 leftProjection = CreatePerspective(65.0f);
        Matrix4x4 rightProjection = CreatePerspective(65.0f);
        Matrix4x4 cyclopsProjection = CreatePerspective(45.0f);

        Quaternion cyclopsRotation =
            Quaternion.CreateFromAxisAngle(Globals.Up, XRMath.DegToRad(28.0f)) *
            Quaternion.CreateFromAxisAngle(Globals.Right, XRMath.DegToRad(11.0f));
        Matrix4x4 cyclopsView = Matrix4x4.CreateFromQuaternion(cyclopsRotation);
        Matrix4x4[] projections = [leftProjection, rightProjection, cyclopsProjection];
        Matrix4x4[] views = [Matrix4x4.CreateTranslation(0.032f, 0.0f, 0.0f), Matrix4x4.CreateTranslation(-0.032f, 0.0f, 0.0f), cyclopsView];

        ProjectionMatrixCombiner.FrustumSolveResult baseline = ProjectionMatrixCombiner.SolveMinimalEnclosingFrustum(
            projections,
            views,
            farBoundsSourceCount: null,
            solveViewOrientation: true,
            refineViewOrientation: false,
            highSpeedMode: false);

        ProjectionMatrixCombiner.FrustumPointCloud pointCloud = ProjectionMatrixCombiner.BuildFrustumPointCloud(projections, views);
        ProjectionMatrixCombiner.FrustumSolveResult highSpeed = ProjectionMatrixCombiner.SolveMinimalEnclosingFrustum(
            pointCloud,
            views,
            solveViewOrientation: true,
            refineViewOrientation: true,
            highSpeedMode: true);

        AssertFrustumContained(highSpeed.View, highSpeed.Projection, GetFrustumCorners(leftProjection, views[0]));
        AssertFrustumContained(highSpeed.View, highSpeed.Projection, GetFrustumCorners(rightProjection, views[1]));
        AssertFrustumContained(highSpeed.View, highSpeed.Projection, GetFrustumCorners(cyclopsProjection, views[2]));
        Assert.That(highSpeed.CandidateLabel, Is.EqualTo(baseline.CandidateLabel));
        Assert.That(GetProjectionCost(highSpeed.Projection), Is.EqualTo(GetProjectionCost(baseline.Projection)).Within(1e-4f));
        Assert.That(highSpeed.WasRefined, Is.False);
    }

    private static Matrix4x4 CreatePerspective(float verticalFovDegrees)
    {
        float fovY = XRMath.DegToRad(verticalFovDegrees);
        float yMax = Near * MathF.Tan(fovY * 0.5f);
        float yMin = -yMax;
        float xMin = yMin * Aspect;
        float xMax = yMax * Aspect;
        return Matrix4x4.CreatePerspectiveOffCenter(xMin, xMax, yMin, yMax, Near, Far);
    }

    private static Vector3[] GetFrustumCorners(Matrix4x4 projection, Matrix4x4 view)
    {
        Assert.That(Matrix4x4.Invert(view * projection, out Matrix4x4 inverseViewProjection), Is.True);

        Vector3[] corners = new Vector3[8];
        int index = 0;
        for (int x = -1; x <= 1; x += 2)
        {
            for (int y = -1; y <= 1; y += 2)
            {
                for (int z = 0; z <= 1; z++)
                {
                    Vector4 point = Vector4.Transform(new Vector4(x, y, z, 1.0f), inverseViewProjection);
                    corners[index++] = new Vector3(point.X, point.Y, point.Z) / point.W;
                }
            }
        }

        return corners;
    }

    private static void AssertFrustumContained(Matrix4x4 combinedProjection, Vector3[] corners)
        => AssertFrustumContained(Matrix4x4.Identity, combinedProjection, corners);

    private static void AssertFrustumContained(Matrix4x4 combinedView, Matrix4x4 combinedProjection, Vector3[] corners)
    {
        foreach (Vector3 corner in corners)
        {
            if (-corner.Z <= 0.001f)
                continue;

            Vector4 clip = Vector4.Transform(Vector4.Transform(new Vector4(corner, 1.0f), combinedView), combinedProjection);
            float invW = 1.0f / clip.W;
            float ndcX = clip.X * invW;
            float ndcY = clip.Y * invW;
            float ndcZ = clip.Z * invW;

            Assert.That(ndcX, Is.InRange(-1.0001f, 1.0001f), $"X outside clip volume for corner {corner}");
            Assert.That(ndcY, Is.InRange(-1.0001f, 1.0001f), $"Y outside clip volume for corner {corner}");
            Assert.That(ndcZ, Is.InRange(-0.0001f, 1.0001f), $"Z outside clip volume for corner {corner}");
        }
    }

    private static float GetFarDistance(Matrix4x4 projection)
    {
        Assert.That(Matrix4x4.Invert(projection, out Matrix4x4 inverseProjection), Is.True);
        Vector4 farCenter = Vector4.Transform(new Vector4(0.0f, 0.0f, 1.0f, 1.0f), inverseProjection);
        farCenter /= farCenter.W;
        return -farCenter.Z;
    }

    private static float GetProjectionCost(Matrix4x4 projection)
    {
        Vector3[] corners = GetFrustumCorners(projection, Matrix4x4.Identity);
        float nearWidth = Vector3.Distance(corners[0], corners[2]);
        float nearHeight = Vector3.Distance(corners[0], corners[1]);
        float nearDist = -corners[0].Z;
        float farDist = -corners[4].Z;
        return nearWidth * nearHeight * ((farDist * farDist * farDist) - (nearDist * nearDist * nearDist)) / (3.0f * nearDist * nearDist);
    }
}