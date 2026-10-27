using System.IO;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Engine.Diagnostics;

/// <summary>Supplies the marker without a runtime assembly dependency.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class HotPathAttributeGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(static output =>
        {
            output.AddEmbeddedAttributeDefinition();
            using Stream stream = typeof(HotPathAttributeGenerator).Assembly
                .GetManifestResourceStream("Engine.Diagnostics.HotPathAttribute.cs")!;
            output.AddSource("HotPathAttribute.g.cs", SourceText.From(stream, Encoding.UTF8, canBeEmbedded: true));
            using Stream trusted = typeof(HotPathAttributeGenerator).Assembly
                .GetManifestResourceStream("Engine.Diagnostics.HotPathTrustedAttribute.cs")!;
            output.AddSource("HotPathTrustedAttribute.g.cs", SourceText.From(trusted, Encoding.UTF8, canBeEmbedded: true));
        });
    }
}
