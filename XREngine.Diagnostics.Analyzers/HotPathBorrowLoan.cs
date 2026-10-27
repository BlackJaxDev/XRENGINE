using Microsoft.CodeAnalysis;

namespace Engine.Diagnostics;

/// <summary>Identifies one mutable reservation and its lexical lifetime.</summary>
internal sealed class HotPathBorrowLoan
{
    public HotPathBorrowLoan(ISymbol root, SyntaxNode scope, SyntaxNode source)
    {
        Root = root;
        Scope = scope;
        Source = source;
    }

    public ISymbol Root { get; }
    public SyntaxNode Scope { get; }
    public SyntaxNode Source { get; }
}
