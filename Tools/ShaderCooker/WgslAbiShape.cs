namespace XREngine.Tools.ShaderCooker;

internal readonly record struct WgslAbiShape(int Alignment, int Size, string Leaf, int LeafOffset, int Stride);
