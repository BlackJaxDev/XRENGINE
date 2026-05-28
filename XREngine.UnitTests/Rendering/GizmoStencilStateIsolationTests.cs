using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class GizmoStencilStateIsolationTests
{
    [TestCase("VPRC_RenderDebugShapes.cs")]
    [TestCase("VPRC_RenderDebugGpuBvh.cs")]
    public void DebugGizmoCommands_ResetStencilStateAfterRendering(string fileName)
    {
        string source = LoadCommandFile(fileName).Replace("\r\n", "\n");

        AssertContainsInOrder(
            source,
            "try",
            "finally",
            "ResetStencilState();");

        string resetMethod = SliceMethod(source, "private static void ResetStencilState()");
        resetMethod.ShouldContain("RuntimeEngine.Rendering.State.EnableStencilTest(false);");
        resetMethod.ShouldContain("RuntimeEngine.Rendering.State.StencilMask(0xFF);");
        resetMethod.ShouldContain("RuntimeEngine.Rendering.State.StencilFunc(EComparison.Always, 0, 0xFF);");
        resetMethod.ShouldContain("RuntimeEngine.Rendering.State.StencilOp(EStencilOp.Keep, EStencilOp.Keep, EStencilOp.Keep);");
    }

    [Test]
    public void GizmoMaterial_UsesStencilBitForPostProcessBypass()
    {
        string material = LoadMaterialFile("XRMaterial.cs").Replace("\r\n", "\n");
        material.ShouldContain("public const uint GizmoStencilBit = 0x80u;");

        string configureMethod = SliceMethod(material, "public static void ConfigureGizmoMaterial(XRMaterial material)");
        AssertContainsInOrder(
            configureMethod,
            "material.RenderPass = (int)EDefaultRenderPass.OnTopForward;",
            "material.ShaderProgramPriority = EProgramPriority.Interactive;",
            "stencil.Enabled = ERenderParamUsage.Enabled;",
            "stencil.FrontFace = MakeGizmoStencilFace(stencil.FrontFace);",
            "stencil.BackFace = MakeGizmoStencilFace(stencil.BackFace);");

        string faceMethod = SliceMethod(material, "private static StencilTestFace MakeGizmoStencilFace(StencilTestFace? source)");
        faceMethod.ShouldContain("Function = EComparison.Always,");
        faceMethod.ShouldContain("Reference = source.Reference | (int)GizmoStencilBit,");
        faceMethod.ShouldContain("ReadMask = source.ReadMask | GizmoStencilBit,");
        faceMethod.ShouldContain("WriteMask = source.WriteMask | GizmoStencilBit,");
        faceMethod.ShouldContain("BothPassOp = EStencilOp.Replace,");
    }

    [Test]
    public void DisabledOpenGlStencilState_KeepsFramebufferStencilClearsWritable()
    {
        string source = LoadOpenGlRendererFile("OpenGLRenderer.RenderParameters.cs").Replace("\r\n", "\n");
        string applyStencilMethod = SliceMethod(source, "private void ApplyStencil(RenderingParameters r)");

        AssertContainsInOrder(
            applyStencilMethod,
            "case ERenderParamUsage.Disabled:",
            "Api.Disable(EnableCap.StencilTest);",
            "Api.StencilMask(0xFF);",
            "Api.StencilOp(GLEnum.Keep, GLEnum.Keep, GLEnum.Keep);",
            "Api.StencilFunc(StencilFunction.Always, 0, 0xFF);");
        applyStencilMethod.ShouldNotContain("Api.StencilMask(0);");
        applyStencilMethod.ShouldNotContain("Api.StencilFunc(StencilFunction.Always, 0, 0);");
    }

    [Test]
    public void FboBindClear_RestoresFullStencilWriteMaskBeforeStencilClear()
    {
        string source = LoadCommandFile(Path.Combine("State", "VPRC_BindFBOByName.cs")).Replace("\r\n", "\n");
        string executeMethod = SliceMethod(source, "protected override void Execute()");

        AssertContainsInOrder(
            executeMethod,
            "bool clearStencil = ClearStencil;",
            "if (clearColor || clearDepth || clearStencil)",
            "if (clearStencil)",
            "RuntimeEngine.Rendering.State.StencilMask(0xFF);",
            "RuntimeEngine.Rendering.State.ClearByBoundFBO(clearColor, clearDepth, clearStencil);");
    }

    private static void AssertContainsInOrder(string source, params string[] expected)
    {
        int previousIndex = -1;
        foreach (string text in expected)
        {
            int index = source.IndexOf(text, previousIndex + 1, StringComparison.Ordinal);
            index.ShouldBeGreaterThan(previousIndex, $"Expected '{text}' after index {previousIndex}.");
            previousIndex = index;
        }
    }

    private static string SliceMethod(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"Could not find method signature '{signature}'.");

        int openBrace = source.IndexOf('{', start);
        openBrace.ShouldBeGreaterThanOrEqualTo(start, $"Could not find method body for '{signature}'.");

        int depth = 0;
        for (int i = openBrace; i < source.Length; i++)
        {
            if (source[i] == '{')
                depth++;
            else if (source[i] == '}')
                depth--;

            if (depth == 0)
                return source[start..(i + 1)];
        }

        throw new InvalidOperationException($"Could not find method end for '{signature}'.");
    }

    private static string LoadCommandFile(string fileName)
    {
        string fullPath = Path.Combine(ResolveRepoRoot(), "XREngine.Runtime.Rendering", "Rendering", "Pipelines", "Commands", fileName);
        File.Exists(fullPath).ShouldBeTrue($"Command file not found: {fullPath}");
        return File.ReadAllText(fullPath);
    }

    private static string LoadMaterialFile(string fileName)
    {
        string fullPath = Path.Combine(ResolveRepoRoot(), "XREngine.Runtime.Rendering", "Objects", "Materials", fileName);
        File.Exists(fullPath).ShouldBeTrue($"Material file not found: {fullPath}");
        return File.ReadAllText(fullPath);
    }

    private static string LoadOpenGlRendererFile(string fileName)
    {
        string fullPath = Path.Combine(ResolveRepoRoot(), "XREngine.Runtime.Rendering", "Rendering", "API", "Rendering", "OpenGL", fileName);
        File.Exists(fullPath).ShouldBeTrue($"OpenGL renderer file not found: {fullPath}");
        return File.ReadAllText(fullPath);
    }

    private static string ResolveRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "XRENGINE.slnx");
            if (File.Exists(candidate))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repo root from test base directory.");
    }
}
