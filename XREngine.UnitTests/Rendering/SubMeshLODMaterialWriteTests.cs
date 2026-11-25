using NUnit.Framework;
using XREngine.Rendering;
using XREngine.Rendering.Models;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class SubMeshLODMaterialWriteTests
{
    [Test]
    public void EqualRequestsAdvanceIntentWithoutPublishingAChange()
    {
        SubMeshLOD lod = new();
        int notices = 0;
        lod.PropertyChanged += (_, change) => { if (change.PropertyName == nameof(SubMeshLOD.Material)) notices++; };
        Assert.That(lod.SetMaterialGuarded<object?>(null, null, _ => { }), Is.False);
        Assert.That(lod.MaterialWriteIntent, Is.EqualTo(1));
        Assert.That(notices, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PrechangeConflictPreventsTheOuterMaterialWrite(bool newerValue)
    {
        XRMaterial requested = new(), newer = new();
        try
        {
            SubMeshLOD lod = new();
            bool current = true, changed = false;
            lod.PropertyChanging += (_, change) =>
            {
                if (changed || change.PropertyName != nameof(SubMeshLOD.Material)) return;
                changed = true;
                if (newerValue) lod.Material = newer;
                else current = false;
            };
            Assert.Throws<InvalidOperationException>(() => lod.SetMaterialGuarded(requested, 0,
                _ => { if (!current) throw new InvalidOperationException("Owner changed."); }));
            Assert.That(lod.Material, Is.SameAs(newerValue ? newer : null));
        }
        finally { requested.Destroy(true); newer.Destroy(true); }
    }

    [Test]
    public void SameValueNestedRequestCancelsTheOlderWrite()
    {
        XRMaterial requested = new();
        try
        {
            SubMeshLOD lod = new();
            bool changed = false;
            lod.PropertyChanging += (_, change) =>
            {
                if (changed || change.PropertyName != nameof(SubMeshLOD.Material)) return;
                changed = true;
                lod.Material = null;
            };
            Assert.Throws<InvalidOperationException>(() => lod.SetMaterialGuarded(requested, 0, _ => { }));
            Assert.That(lod.Material, Is.Null);
        }
        finally { requested.Destroy(true); }
    }

    [Test]
    public void ObserverFailureLeavesTheAcceptedMaterialVisible()
    {
        XRMaterial requested = new();
        try
        {
            SubMeshLOD lod = new();
            lod.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(SubMeshLOD.Material)) throw new ApplicationException("Observer failed.");
            };
            Assert.Throws<ApplicationException>(() => lod.SetMaterialGuarded(requested, 0, _ => { }));
            Assert.That(lod.Material, Is.SameAs(requested));
        }
        finally { requested.Destroy(true); }
    }
}
