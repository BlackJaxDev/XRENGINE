using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RendererBackendCatalogTests
{
    [Test]
    public void RendererBackendId_NormalizesForStableCrossAssemblyIdentity()
    {
        new RendererBackendId("  OpenGL  ").ShouldBe(RendererBackendId.OpenGL);
        new RendererBackendId("VULKAN").ShouldBe(RendererBackendId.Vulkan);
    }

    [Test]
    public void Register_CreateAndLeaseTeardown_UseFactoryContract()
    {
        using RendererBackendCatalog catalog = new();
        RendererBackendTestLifecycle lifecycle = new();
        IRuntimeRendererHost expectedRenderer = CreateProxy<IRuntimeRendererHost, RendererBackendTestProxy>();
        RendererBackendTestFactory factory = new(expectedRenderer);
        RendererBackendRegistration registration = CreateRegistration(
            RendererBackendId.OpenGL,
            RendererBackendCapabilities.DesktopPresentation,
            factory,
            lifecycle);

        IDisposable lease = catalog.Register(registration);
        IRuntimeRenderWindowHost window = CreateProxy<IRuntimeRenderWindowHost, RendererBackendTestProxy>();
        IRuntimeRendererHost renderer = catalog.CreateRequired(
            RuntimeGraphicsApiKind.OpenGL,
            new RendererBackendCreateContext(window),
            RendererBackendCapabilities.DesktopPresentation);

        renderer.ShouldBeSameAs(expectedRenderer);
        factory.LastWindow.ShouldBeSameAs(window);
        lifecycle.RegisteredCount.ShouldBe(1);
        catalog.Count.ShouldBe(1);

        lease.Dispose();
        catalog.Count.ShouldBe(0);
        lifecycle.UnregisteredCount.ShouldBe(1);
    }

    [Test]
    public void CollectibleModuleContract_RegistersAndReleasesWithoutDynamicLoading()
    {
        using RendererBackendCatalog catalog = new();
        RendererBackendTestModule module = new(
            CreateRegistration(RendererBackendId.OpenGL));

        IDisposable lease = catalog.Register(module);
        catalog.GetRequired(RendererBackendId.OpenGL).Factory.ShouldBeSameAs(module.Factory);
        module.RegisteredCount.ShouldBe(1);

        lease.Dispose();
        module.UnregisteredCount.ShouldBe(1);
        catalog.Count.ShouldBe(0);
    }

    [Test]
    public void DuplicateRegistration_IsRejectedWithActionableDiagnostic()
    {
        using RendererBackendCatalog catalog = new();
        using IDisposable lease = catalog.Register(CreateRegistration(RendererBackendId.OpenGL));

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => catalog.Register(CreateRegistration(RendererBackendId.OpenGL)));

        exception.Message.ShouldContain("already registered");
        exception.Message.ShouldContain(nameof(RendererBackendRegistrationBehavior.ReplaceExisting));
        catalog.Count.ShouldBe(1);
    }

    [Test]
    public void ExplicitReplacement_TearsDownOldModule_AndOldLeaseCannotRemoveReplacement()
    {
        using RendererBackendCatalog catalog = new();
        RendererBackendTestLifecycle oldLifecycle = new();
        RendererBackendTestLifecycle newLifecycle = new();
        IDisposable oldLease = catalog.Register(
            CreateRegistration(RendererBackendId.OpenGL, lifecycle: oldLifecycle));

        using IDisposable newLease = catalog.Register(
            CreateRegistration(RendererBackendId.OpenGL, lifecycle: newLifecycle),
            RendererBackendRegistrationBehavior.ReplaceExisting);

        oldLifecycle.UnregisteredCount.ShouldBe(1);
        newLifecycle.RegisteredCount.ShouldBe(1);
        oldLease.Dispose();
        catalog.Count.ShouldBe(1);
        newLifecycle.UnregisteredCount.ShouldBe(0);

        newLease.Dispose();
        catalog.Count.ShouldBe(0);
        newLifecycle.UnregisteredCount.ShouldBe(1);
    }

    [Test]
    public void MissingBackend_ListsInstalledModulesAndCompositionFix()
    {
        using RendererBackendCatalog catalog = new();
        using IDisposable lease = catalog.Register(CreateRegistration(RendererBackendId.OpenGL));

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => catalog.GetRequired(RendererBackendId.Vulkan));

        exception.Message.ShouldContain("vulkan");
        exception.Message.ShouldContain("opengl");
        exception.Message.ShouldContain("composition root");
    }

    [Test]
    public void MissingRequiredCapability_FailsBeforeFactoryInvocation()
    {
        using RendererBackendCatalog catalog = new();
        RendererBackendTestFactory factory = new(CreateProxy<IRuntimeRendererHost, RendererBackendTestProxy>());
        using IDisposable lease = catalog.Register(
            CreateRegistration(
                RendererBackendId.OpenGL,
                RendererBackendCapabilities.DesktopPresentation,
                factory));

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => catalog.CreateRequired(
                RuntimeGraphicsApiKind.OpenGL,
                new RendererBackendCreateContext(CreateProxy<IRuntimeRenderWindowHost, RendererBackendTestProxy>()),
                RendererBackendCapabilities.OpenXrPresentation));

        exception.Message.ShouldContain(nameof(RendererBackendCapabilities.OpenXrPresentation));
        exception.Message.ShouldContain(nameof(RendererBackendCapabilities.DesktopPresentation));
        factory.CreateCount.ShouldBe(0);
    }

    [Test]
    public void CatalogDispose_TearsDownEveryModuleOnce()
    {
        RendererBackendCatalog catalog = new();
        RendererBackendTestLifecycle first = new();
        RendererBackendTestLifecycle second = new();
        _ = catalog.Register(CreateRegistration(RendererBackendId.OpenGL, lifecycle: first));
        _ = catalog.Register(CreateRegistration(RendererBackendId.Vulkan, lifecycle: second));

        catalog.Dispose();
        catalog.Dispose();

        first.UnregisteredCount.ShouldBe(1);
        second.UnregisteredCount.ShouldBe(1);
        catalog.Count.ShouldBe(0);
    }

    [Test]
    public void BuiltInRegistration_IsExplicitAndPublishesReloadLimits()
    {
        using RendererBackendCatalog catalog = new();
        using IDisposable registrations = BuiltInRendererBackendModules.RegisterAll(catalog);

        RendererBackendRegistration openGl = catalog.GetRequired(RendererBackendId.OpenGL);
        RendererBackendRegistration vulkan = catalog.GetRequired(RendererBackendId.Vulkan);

        openGl.Factory.ShouldBeOfType<OpenGLRendererBackendFactory>();
        vulkan.Factory.ShouldBeOfType<VulkanRendererBackendFactory>();
        openGl.Metadata.ReloadLimitations.ShouldNotBe(RendererBackendReloadLimitations.None);
        openGl.Metadata.ReloadLimitationDescription.ShouldNotBeNullOrWhiteSpace();
        catalog.Count.ShouldBe(2);
    }

    private static RendererBackendRegistration CreateRegistration(
        RendererBackendId id,
        RendererBackendCapabilities capabilities = RendererBackendCapabilities.None,
        IRendererBackendFactory? factory = null,
        IRendererBackendLifecycle? lifecycle = null)
        => new(
            new RendererBackendMetadata(
                id,
                id == RendererBackendId.Vulkan
                    ? RuntimeGraphicsApiKind.Vulkan
                    : RuntimeGraphicsApiKind.OpenGL,
                $"Test {id}",
                new Version(1, 0),
                capabilities,
                RendererBackendReloadLimitations.None),
            factory ?? new RendererBackendTestFactory(CreateProxy<IRuntimeRendererHost, RendererBackendTestProxy>()),
            lifecycle);

    private static TInterface CreateProxy<TInterface, TProxy>()
        where TInterface : class
        where TProxy : DispatchProxy
        => DispatchProxy.Create<TInterface, TProxy>();

}
