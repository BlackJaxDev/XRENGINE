using System;

namespace XREngine;

/// <summary>
/// Forces cooked-binary object fallback to use reflection encoding instead of probing MemoryPack.
/// Use this for runtime graph objects whose persistent state is reflection-serializable, but whose
/// transient fields make MemoryPack generation inappropriate or intentionally unavailable.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = true, AllowMultiple = false)]
public sealed class CookedBinaryReflectionOnlyAttribute : Attribute
{
}
