using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class ShaderSourceDependencyHotReloadTests
{
    [SetUp]
    public void SetUp()
        => ShaderSourceDependencyIndex.ResetForTests();

    [TearDown]
    public void TearDown()
        => ShaderSourceDependencyIndex.ResetForTests();

    [Test]
    public void IncludeChange_InvalidatesOnlyDependentLoadedShaders()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "shader-hot-reload",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        string rootPath = Path.Combine(directory, "root.frag");
        string includePath = Path.Combine(directory, "shared.glsl");
        string unrelatedPath = Path.Combine(directory, "unrelated.glsl");
        try
        {
            File.WriteAllText(rootPath, "#include \"shared.glsl\"\nvoid main() {}\n");
            File.WriteAllText(includePath, "const float Value = 1.0;\n");
            File.WriteAllText(unrelatedPath, "const float Other = 1.0;\n");

            TextFile source = new(rootPath)
            {
                Text = File.ReadAllText(rootPath),
            };
            XRShader shader = new(EShaderType.Fragment, source);
            shader.TryGetResolvedShaderSource(out _, logFailures: false).ShouldBeTrue();

            int sourceChanged = 0;
            shader.SourceChanged += _ => sourceChanged++;
            long originalRevision = shader.SourceRevision;

            ShaderSourceDependencyIndex.ProcessFileChangeImmediately(
                new(includePath, ShaderSourceFileChangeKind.Changed)).ShouldBe(1);

            sourceChanged.ShouldBe(1);
            shader.SourceRevision.ShouldBe(originalRevision + 1);

            ShaderSourceDependencyIndex.ProcessFileChangeImmediately(
                new(unrelatedPath, ShaderSourceFileChangeKind.Changed)).ShouldBe(0);
            sourceChanged.ShouldBe(1);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void SourceRevision_IsMonotonicForTextTypeAndDependencyChanges()
    {
        XRShader shader = new(EShaderType.Fragment, TextFile.FromText("void main() {}"));
        long initial = shader.SourceRevision;

        shader.Source.Text = "void main() { }\n";
        long afterText = shader.SourceRevision;
        shader.Type = EShaderType.Compute;
        long afterType = shader.SourceRevision;
        shader.NotifySourceDependencyChanged("test dependency");

        afterText.ShouldBeGreaterThan(initial);
        afterType.ShouldBeGreaterThan(afterText);
        shader.SourceRevision.ShouldBeGreaterThan(afterType);
    }

    [TestCase(EShaderType.Vertex)]
    [TestCase(EShaderType.Fragment)]
    [TestCase(EShaderType.Geometry)]
    [TestCase(EShaderType.TessControl)]
    [TestCase(EShaderType.TessEvaluation)]
    [TestCase(EShaderType.Compute)]
    [TestCase(EShaderType.Task)]
    [TestCase(EShaderType.Mesh)]
    public void EverySupportedStage_UsesTheSameMonotonicReloadContract(EShaderType stage)
    {
        XRShader shader = new(stage, TextFile.FromText("void main() {}"));
        long initial = shader.SourceRevision;
        int notifications = 0;
        shader.SourceChanged += _ => notifications++;

        shader.Source.Text = "void main() { }\n";
        long afterText = shader.SourceRevision;
        shader.NotifySourceDependencyChanged("transitive include");

        afterText.ShouldBeGreaterThan(initial);
        shader.SourceRevision.ShouldBeGreaterThan(afterText);
        notifications.ShouldBe(2);
    }
}
