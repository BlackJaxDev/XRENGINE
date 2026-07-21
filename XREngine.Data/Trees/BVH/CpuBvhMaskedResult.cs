namespace XREngine.Data.Trees;

public readonly record struct CpuBvhMaskedResult<T>(T Item, ulong ExactViewMask);
