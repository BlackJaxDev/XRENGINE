using XREngine.Data.Core;

namespace XREngine.UnitTests.Core;

/// <summary>Checks ownership of allocations that never enter the object cache.</summary>
internal sealed class NonCacheConstructionProbe : XRObjectBase
{
    protected override bool ParticipatesInObjectCache => false;
}
