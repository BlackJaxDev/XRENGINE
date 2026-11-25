using NUnit.Framework;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class UIIndexedDrawScopeTests
{
    private static UIIndexedDrawIntent Intent()
        => new(new XRMeshRenderer(), 4, EPrimitiveType.Triangles, IndexSize.TwoBytes, 6, 1, 0);

    [Test]
    public void Scope_RequiresEveryExactNativeDrawOnce()
    {
        UIIndexedDrawIntent triangle = Intent();
        UIIndexedDrawIntent line = triangle with { Topology = EPrimitiveType.Lines, IndexCount = 2 };
        UIIndexedDrawScopeState scope = default;
        Assert.That(scope.TryBegin(1, 2, 3, [triangle, line]), Is.True);
        Assert.That(scope.Acknowledge(1, 2, 3, line, true), Is.True);
        Assert.That(scope.Acknowledge(1, 2, 3, triangle, true), Is.True);
        Assert.That(scope.Complete(1, 2, 3), Is.True);
        Assert.That(scope.ScopeId, Is.Zero);
        Assert.That(scope.Complete(1, 2, 3), Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Scope_MissingOrRepeatedNativeDrawCannotComplete(bool repeated)
    {
        UIIndexedDrawIntent intent = Intent();
        UIIndexedDrawScopeState scope = default;
        Assert.That(scope.TryBegin(1, 2, 3, [intent]), Is.True);
        if (repeated)
        {
            Assert.That(scope.Acknowledge(1, 2, 3, intent, true), Is.True);
            Assert.That(scope.Acknowledge(1, 2, 3, intent, true), Is.False);
        }
        Assert.That(scope.Complete(1, 2, 3), Is.False);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [TestCase(6)]
    [TestCase(7)]
    public void Scope_RejectsMismatchedNativeTuple(int field)
    {
        UIIndexedDrawIntent intent = Intent();
        UIIndexedDrawIntent actual = field switch
        {
            0 => intent with { Renderer = new XRMeshRenderer() },
            1 => intent with { GeometryRevision = intent.GeometryRevision + 1 },
            2 => intent with { Topology = EPrimitiveType.Lines },
            3 => intent with { ElementType = IndexSize.FourBytes },
            4 => intent with { IndexCount = intent.IndexCount + 1 },
            5 => intent with { Instances = intent.Instances + 1 },
            6 => intent with { IndexOffset = 2 },
            _ => intent,
        };
        UIIndexedDrawScopeState scope = default;
        Assert.That(scope.TryBegin(1, 2, 3, [intent]), Is.True);
        Assert.That(scope.Acknowledge(1, 2, 3, actual, field != 7), Is.False);
        Assert.That(scope.Complete(1, 2, 3), Is.False);
    }

    [TestCase(10UL, 2UL, 3L)]
    [TestCase(1UL, 20UL, 3L)]
    [TestCase(1UL, 2UL, 30L)]
    public void Scope_RejectsOtherReceiptOrBackendGeneration(ulong token, ulong receipt, long generation)
    {
        UIIndexedDrawIntent intent = Intent();
        UIIndexedDrawScopeState scope = default;
        Assert.That(scope.TryBegin(1, 2, 3, [intent]), Is.True);
        Assert.That(scope.Acknowledge(token, receipt, generation, intent, true), Is.False);
        Assert.That(scope.Complete(1, 2, 3), Is.False);
    }

    [Test]
    public void Scope_RejectsNestedOrDuplicateExpectations()
    {
        UIIndexedDrawIntent intent = Intent();
        UIIndexedDrawScopeState scope = default;
        Assert.That(scope.TryBegin(1, 2, 3, [intent, intent]), Is.False);
        Assert.That(scope.TryBegin(1, 2, 3, []), Is.False);
        Assert.That(scope.TryBegin(1, 2, 3, [intent]), Is.True);
        Assert.That(scope.TryBegin(4, 2, 3, [intent]), Is.False);
        Assert.That(scope.Acknowledge(1, 2, 3, intent, true), Is.False);
        Assert.That(scope.Complete(1, 2, 3), Is.False);
    }
}
