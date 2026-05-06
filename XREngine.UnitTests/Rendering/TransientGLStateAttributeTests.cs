using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.OpenGL;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Phase 5 regression guard. The original UI text material churn
/// (`Combined:UIBatchTextMaterial` rebuilt every frame) was caused by
/// `GLObjectBase.OnPropertyChanged` invalidating the GL VAO and shader
/// programs on transient `BuffersBound` toggles. The fix introduced
/// `TransientGLStateAttribute` and tagged `GLMeshRenderer.BuffersBound` as
/// transient. These tests pin that contract so the attribute and tag cannot
/// be silently removed.
/// </summary>
[TestFixture]
public sealed class TransientGLStateAttributeTests
{
    [Test]
    public void TransientGLStateAttribute_OnlyTargetsProperties_AndIsInherited()
    {
        var usage = typeof(TransientGLStateAttribute).GetCustomAttribute<AttributeUsageAttribute>();
        usage.ShouldNotBeNull();
        usage.ValidOn.ShouldBe(AttributeTargets.Property);
        usage.Inherited.ShouldBeTrue();
        usage.AllowMultiple.ShouldBeFalse();
    }

    [Test]
    public void GLMeshRenderer_BuffersBound_IsTaggedTransient()
    {
        // GLMeshRenderer is nested under OpenGLRenderer, so we resolve it via
        // the rendering assembly and reflection.
        Type? meshRenderer = typeof(OpenGLRenderer).Assembly
            .GetType("XREngine.Rendering.OpenGL.OpenGLRenderer+GLMeshRenderer");
        meshRenderer.ShouldNotBeNull();

        PropertyInfo? buffersBound = meshRenderer!.GetProperty(
            "BuffersBound",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        buffersBound.ShouldNotBeNull();
        buffersBound!.IsDefined(typeof(TransientGLStateAttribute), inherit: true)
            .ShouldBeTrue("BuffersBound must remain [TransientGLState] to prevent UI text material rebuild churn (Phase 5 regression).");
    }
}
