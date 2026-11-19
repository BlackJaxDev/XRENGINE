using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using XREngine.ShaderTranslation.Preprocessing;

namespace XREngine.UnitTests.Rendering.ShaderTranslation;

[TestFixture]
public sealed class ShaderIncludeExpanderTests
{
    private string _root = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "xre-include-expander-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "Shaders"));
        Directory.CreateDirectory(Path.Combine(_root, "Project", "Assets", "Lib"));
        File.WriteAllText(Path.Combine(_root, "Project", "Assets", "Lib", "Common.cginc"), "#pragma once\nfloat common;\n#include \"Nested.cginc\"\n");
        File.WriteAllText(Path.Combine(_root, "Project", "Assets", "Lib", "Nested.cginc"), "float nested;\n");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void Expand_ProjectInclude_InsertsTextWithLineDirectives()
    {
        ShaderIncludeExpander expander = new([Path.Combine(_root, "Project")]);
        const string unit = "#line 10 \"Eye.shader\"\nfloat a;\n#include \"Assets/Lib/Common.cginc\"\nfloat b;\n#include \"Missing.cginc\"\n";

        string expanded = expander.Expand(unit, Path.Combine(_root, "Shaders"), "Eye.shader");

        string common = Path.Combine(_root, "Project", "Assets", "Lib", "Common.cginc").Replace('\\', '/');
        string nested = Path.Combine(_root, "Project", "Assets", "Lib", "Nested.cginc").Replace('\\', '/');
        expanded.ShouldContain($"#line 1 \"{common}\"\n\nfloat common;\n#line 1 \"{nested}\"\nfloat nested;\n#line 4 \"{common}\"\n");
        expanded.ShouldContain("#endif\n#line 12 \"Eye.shader\"\nfloat b;\n#include \"Missing.cginc\"\n");
        expanded.ShouldContain("#ifndef XR_INCLUDE_ONCE_");
        expander.IncludedFiles.Count.ShouldBe(2);
    }

    [Test]
    public void Expand_RecursiveInclude_Throws()
    {
        File.WriteAllText(Path.Combine(_root, "Shaders", "Loop.cginc"), "#include \"Loop.cginc\"\n");
        ShaderIncludeExpander expander = new([]);

        Should.Throw<InvalidOperationException>(() => expander.Expand("#include \"Loop.cginc\"\n", Path.Combine(_root, "Shaders"), "Loop.shader"));
    }

    [Test]
    public void Expand_IncludeInBlockComment_IsKept()
    {
        ShaderIncludeExpander expander = new([Path.Combine(_root, "Project")]);
        const string unit = "/*\n#include \"Assets/Lib/Nested.cginc\"\n*/\n";

        expander.Expand(unit, _root, "A.shader").ShouldBe(unit);
    }
}
