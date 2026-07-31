using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RendererHostContextTests
{
    [Test]
    public void PresentationlessContext_ExposesTargetStateWithoutDesktopServices()
    {
        PresentationlessRenderTarget target = new(640, 360, FrameSlotCount: 2);
        RendererHostContext context = new(target, backendGeneration: 17);

        context.Target.ShouldBeSameAs(target);
        context.ExecutionMode.ShouldBe(RenderExecutionMode.Presentationless);
        context.OutputProperties.ShouldNotBeNull();
        context.OutputProperties!.Value.Width.ShouldBe(640u);
        context.OutputProperties.Value.Height.ShouldBe(360u);
        context.BackendGeneration.ShouldBe(17);
        context.HasDesktopWindowServices.ShouldBeFalse();
        context.TryGetDesktopWindowHost(out IRuntimeRenderWindowHost? window).ShouldBeFalse();
        window.ShouldBeNull();
        context.BuildDiagnosticIdentity().ShouldBe("Presentationless:640x360x1");

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            context.RequireDesktopWindowHost);
        exception.Message.ShouldContain(nameof(RenderExecutionMode.Presentationless));
        exception.Message.ShouldContain(nameof(RendererHostContext.TryGetDesktopWindowHost));
    }

    [Test]
    public void NonWindowContext_RejectsDesktopLinkOwnership()
    {
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => new RendererHostContext(
                new PresentationlessRenderTarget(32, 32),
                linkRendererToDesktopWindow: true));

        exception.Message.ShouldContain(nameof(IRendererDesktopWindowServices));
    }

    [Test]
    public void DesktopContext_PreservesWindowAndCompatibilityState()
    {
        IRuntimeRenderWindowHost window =
            DispatchProxy.Create<IRuntimeRenderWindowHost, RendererBackendTestProxy>();
        RendererHostContext context = RendererHostContext.CreateDesktop(
            window,
            linkRendererToDesktopWindow: true,
            backendGeneration: 29);

        context.ExecutionMode.ShouldBe(RenderExecutionMode.DesktopWsi);
        context.HasDesktopWindowServices.ShouldBeTrue();
        context.LinkRendererToDesktopWindow.ShouldBeTrue();
        context.BackendGeneration.ShouldBe(29);
        context.TryGetDesktopWindowHost(out IRuntimeRenderWindowHost? resolved).ShouldBeTrue();
        resolved.ShouldBeSameAs(window);
        context.RequireDesktopWindowHost().ShouldBeSameAs(window);
        context.RequireDesktopWindow<IRuntimeRenderWindowHost>().ShouldBeSameAs(window);
    }

    [Test]
    public void DesktopContext_RejectsWrongConcreteWindowTypeAtBoundary()
    {
        IRuntimeRenderWindowHost window =
            DispatchProxy.Create<IRuntimeRenderWindowHost, RendererBackendTestProxy>();
        RendererHostContext context = RendererHostContext.CreateDesktop(window);

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            context.RequireDesktopWindow<XRWindow>);

        exception.Message.ShouldContain(window.GetType().FullName!);
        exception.Message.ShouldContain(typeof(XRWindow).FullName!);
    }

    [Test]
    public void BackendCreateContext_FreezesIntoRendererOwnedContext()
    {
        ComponentRenderTarget target = new(
            "descriptor-update",
            new RenderTargetOutputProperties(128, 64, FrameSlotCount: 3));
        RendererBackendCreateContext createContext = new(
            target,
            linkRendererToWindow: false,
            moduleGeneration: 41);

        RendererHostContext context = createContext.ToRendererHostContext();

        context.Target.ShouldBeSameAs(target);
        context.ExecutionMode.ShouldBe(RenderExecutionMode.Component);
        context.LinkRendererToDesktopWindow.ShouldBeFalse();
        context.BackendGeneration.ShouldBe(41);
    }

    [Test]
    public void RendererBaseAndProductionBackends_ExposeTargetFirstConstruction()
    {
        typeof(TargetFirstRendererProbe).IsSubclassOf(typeof(AbstractRenderer)).ShouldBeTrue();
        typeof(TargetFirstRendererProbe)
            .GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                [typeof(RendererHostContext)],
                modifiers: null)
            .ShouldNotBeNull();

        typeof(OpenGLRenderer).GetConstructor([typeof(RendererHostContext)]).ShouldNotBeNull();
        typeof(VulkanRenderer).GetConstructor([typeof(RendererHostContext)]).ShouldNotBeNull();
        typeof(OpenGLRenderer).GetConstructor([typeof(XRWindow), typeof(bool), typeof(long)]).ShouldNotBeNull();
        typeof(VulkanRenderer).GetConstructor([typeof(XRWindow), typeof(bool), typeof(long)]).ShouldNotBeNull();
    }

    private abstract class TargetFirstRendererProbe : AbstractRenderer
    {
        protected TargetFirstRendererProbe(RendererHostContext hostContext)
            : base(hostContext)
        {
        }
    }
}
