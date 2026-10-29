using NUnit.Framework;
using Shouldly;
using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Rendering.Models.Gaussian.Baking.Views;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class BakeViewPlannerTests
{
    private static readonly Vector3 Center = new(1.0f, 2.0f, -3.0f);

    [Test]
    public void PlanOrbitViews_Returns32DistinctViewsAimedAtCenter()
    {
        var views = BakeViewPlanner.PlanOrbitViews(Box(2f), 32);

        views.Count.ShouldBe(32);
        views.ShouldAllBe(v => Vector3.Dot(Forward(v), Vector3.Normalize(Center - v.Position)) > 0.9999f);
        MinPairwiseAngleDegrees(views).ShouldBeGreaterThan(20f);
        views.ShouldBe(BakeViewPlanner.PlanOrbitViews(Box(2f), 32));
    }

    [Test]
    public void PlanOrbitViews_BoundingSphereFitsVerticalFov()
    {
        AABB bounds = Box(2f);
        float radius = bounds.Size.Length() * 0.5f;
        float minimumDistance = radius / MathF.Sin(20.0f * MathF.PI / 180.0f);

        var views = BakeViewPlanner.PlanOrbitViews(bounds, 32);

        views.ShouldAllBe(v => v.VerticalFovDegrees == 40f);
        views.ShouldAllBe(v => Vector3.Distance(v.Position, Center) >= minimumDistance);
    }

    [Test]
    public void PlanOrbitViews_ViewNearPole_UsesZUp()
    {
        var views = BakeViewPlanner.PlanOrbitViews(Box(2f), 200);

        // The first Fibonacci direction is about 5.7 degrees from +Y, inside the 10 degree pole cone.
        BakeCameraPose pole = views[0];
        float angleFromY = MathF.Acos(MathF.Abs(Vector3.Dot(Forward(pole), Vector3.UnitY))) * 180.0f / MathF.PI;
        angleFromY.ShouldBeLessThan(10f);
        Vector3.Dot(Up(pole), Vector3.UnitZ).ShouldBeGreaterThan(0.9f);
        views.ShouldAllBe(v => float.IsFinite(v.Rotation.X) && float.IsFinite(v.Rotation.Y)
            && float.IsFinite(v.Rotation.Z) && float.IsFinite(v.Rotation.W));
    }

    [Test]
    public void PlanOrbitViews_ViewAwayFromPole_UsesYUp()
    {
        var views = BakeViewPlanner.PlanOrbitViews(Box(2f), 32);

        BakeCameraPose equator = views[16];
        Vector3.Dot(Up(equator), Vector3.UnitY).ShouldBeGreaterThan(0.9f);
    }

    [Test]
    public void PlanInteriorViews_LooksOutwardFromCenter()
    {
        var views = BakeViewPlanner.PlanInteriorViews(Box(2f), 32);

        views.Count.ShouldBe(32);
        views.ShouldAllBe(v => v.Position == Center);
        views.ShouldAllBe(v => v.VerticalFovDegrees == 75f);
        MinPairwiseAngleDegrees(views).ShouldBeGreaterThan(20f);

        var orbit = BakeViewPlanner.PlanOrbitViews(Box(2f), 32);
        for (int i = 0; i < views.Count; i++)
            Vector3.Dot(Forward(views[i]), Vector3.Normalize(orbit[i].Position - Center)).ShouldBeGreaterThan(0.9999f);
    }

    private static AABB Box(float size)
        => AABB.FromCenterSize(Center, new Vector3(size));

    private static Vector3 Forward(BakeCameraPose pose)
        => Vector3.Transform(-Vector3.UnitZ, pose.Rotation);

    private static Vector3 Up(BakeCameraPose pose)
        => Vector3.Transform(Vector3.UnitY, pose.Rotation);

    private static float MinPairwiseAngleDegrees(IReadOnlyList<BakeCameraPose> views)
    {
        float minimum = float.MaxValue;
        for (int i = 0; i < views.Count; i++)
        {
            for (int j = i + 1; j < views.Count; j++)
            {
                float cosine = Math.Clamp(Vector3.Dot(Forward(views[i]), Forward(views[j])), -1.0f, 1.0f);
                minimum = MathF.Min(minimum, MathF.Acos(cosine) * 180.0f / MathF.PI);
            }
        }

        return minimum;
    }
}
