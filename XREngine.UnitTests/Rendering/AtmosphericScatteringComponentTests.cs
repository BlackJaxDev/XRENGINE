using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Scene.Environment;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AtmosphericScatteringComponentTests
{
    [SetUp]
    public void SetUp()
        => AtmosphericScatteringComponent.Registry.ClearForTests();

    [TearDown]
    public void TearDown()
        => AtmosphericScatteringComponent.Registry.ClearForTests();

    [Test]
    public void AuthoringProperties_ClampToValidAtmosphereRanges()
    {
        var atmosphere = new AtmosphericScatteringComponent
        {
            GroundRadius = -1.0f,
            AtmosphereHeight = -2.0f,
            RayleighScaleHeight = -3.0f,
            MieScaleHeight = -4.0f,
            SunIntensity = -5.0f,
            MieAnisotropy = 2.0f,
            GroundAlbedo = 4.0f,
            ExposureScale = -6.0f,
            RayleighScattering = new Vector3(-1.0f, 2.0f, -3.0f),
            MieScattering = new Vector3(-4.0f, -5.0f, 6.0f),
        };

        atmosphere.GroundRadius.ShouldBeGreaterThan(0.0f);
        atmosphere.AtmosphereHeight.ShouldBeGreaterThan(0.0f);
        atmosphere.RayleighScaleHeight.ShouldBeGreaterThan(0.0f);
        atmosphere.MieScaleHeight.ShouldBeGreaterThan(0.0f);
        atmosphere.SunIntensity.ShouldBe(0.0f);
        atmosphere.MieAnisotropy.ShouldBe(0.99f);
        atmosphere.GroundAlbedo.ShouldBe(1.0f);
        atmosphere.ExposureScale.ShouldBe(0.0f);
        atmosphere.RayleighScattering.ShouldBe(new Vector3(0.0f, 2.0f, 0.0f));
        atmosphere.MieScattering.ShouldBe(new Vector3(0.0f, 0.0f, 6.0f));
    }

    [Test]
    public void Registry_DoesNotSelectDisabledComponent()
    {
        RuntimeRenderWorldInstance world = CreateWorldWithAtmospheres(out AtmosphericScatteringComponent atmosphere);

        atmosphere.Enabled = false;

        bool selected = AtmosphericScatteringComponent.Registry.TryGetBestActive(world, Vector3.Zero, out var active);

        selected.ShouldBeFalse();
        active.ShouldBeNull();
    }

    [Test]
    public void Registry_DoesNotSelectInactiveRegisteredComponent()
    {
        RuntimeRenderWorldInstance world = new();
        var node = new SceneNode("InactiveAtmosphere");
        AtmosphericScatteringComponent atmosphere = node.AddComponent<AtmosphericScatteringComponent>()!;

        AtmosphericScatteringComponent.Registry.Register(world, atmosphere);

        bool selected = AtmosphericScatteringComponent.Registry.TryGetBestActive(world, Vector3.Zero, out var active);

        selected.ShouldBeFalse();
        active.ShouldBeNull();
    }

    [Test]
    public void Registry_HigherPriorityWinsWhenCameraIsInsideBothAtmospheres()
    {
        RuntimeRenderWorldInstance world = CreateWorldWithAtmospheres(
            out AtmosphericScatteringComponent lowerPriority,
            out AtmosphericScatteringComponent higherPriority);

        lowerPriority.Priority = 1;
        higherPriority.Priority = 10;

        bool selected = AtmosphericScatteringComponent.Registry.TryGetBestActive(world, Vector3.Zero, out var active);

        selected.ShouldBeTrue();
        active.ShouldBeSameAs(higherPriority);
    }

    [Test]
    public void Registry_CameraInsideAtmosphereWinsOverOutsideHigherPriorityCandidate()
    {
        RuntimeRenderWorldInstance world = CreateWorldWithAtmospheres(
            out AtmosphericScatteringComponent insideCandidate,
            out AtmosphericScatteringComponent outsideCandidate,
            secondTranslation: new Vector3(100.0f, 0.0f, 0.0f));

        insideCandidate.Priority = -10;
        outsideCandidate.Priority = 100;

        bool selected = AtmosphericScatteringComponent.Registry.TryGetBestActive(world, Vector3.Zero, out var active);

        selected.ShouldBeTrue();
        active.ShouldBeSameAs(insideCandidate);
    }

    [Test]
    public void Registry_CopyActiveClampsDestinationSizeAndPreservesSortOrder()
    {
        RuntimeRenderWorldInstance world = new();
        SceneNode root = new("Root");

        for (int i = 0; i < 6; i++)
        {
            AtmosphericScatteringComponent component = AddAtmosphere(root, $"Atmosphere{i}");
            component.Priority = i;
        }

        Activate(world, root);
        AtmosphericScatteringComponent?[] active = new AtmosphericScatteringComponent?[4];

        int count = AtmosphericScatteringComponent.Registry.CopyActive(world, Vector3.Zero, active);

        count.ShouldBe(4);
        active[0]!.Priority.ShouldBe(5);
        active[1]!.Priority.ShouldBe(4);
        active[2]!.Priority.ShouldBe(3);
        active[3]!.Priority.ShouldBe(2);
    }

    private static RuntimeRenderWorldInstance CreateWorldWithAtmospheres(out AtmosphericScatteringComponent atmosphere)
    {
        RuntimeRenderWorldInstance world = new();
        SceneNode root = new("Root");
        atmosphere = AddAtmosphere(root, "Atmosphere");
        Activate(world, root);
        return world;
    }

    private static RuntimeRenderWorldInstance CreateWorldWithAtmospheres(
        out AtmosphericScatteringComponent first,
        out AtmosphericScatteringComponent second,
        Vector3 secondTranslation = default)
    {
        RuntimeRenderWorldInstance world = new();
        SceneNode root = new("Root");
        first = AddAtmosphere(root, "AtmosphereA");
        second = AddAtmosphere(root, "AtmosphereB", secondTranslation);
        Activate(world, root);
        return world;
    }

    private static AtmosphericScatteringComponent AddAtmosphere(
        SceneNode root,
        string name,
        Vector3 translation = default)
    {
        SceneNode node = new(root, name);
        if (node.Transform is Transform transform)
            transform.Translation = translation;

        node.Transform.RecalculateMatrixHierarchy(forceWorldRecalc: true, setRenderMatrixNow: true, ELoopType.Sequential)
            .GetAwaiter()
            .GetResult();

        AtmosphericScatteringComponent atmosphere = node.AddComponent<AtmosphericScatteringComponent>()!;
        atmosphere.GroundRadius = 10.0f;
        atmosphere.AtmosphereHeight = 5.0f;
        return atmosphere;
    }

    private static void Activate(RuntimeRenderWorldInstance world, SceneNode root)
    {
        root.Transform.RecalculateMatrixHierarchy(forceWorldRecalc: true, setRenderMatrixNow: true, ELoopType.Sequential)
            .GetAwaiter()
            .GetResult();
        world.RootNodes.Add(root);
    }
}
