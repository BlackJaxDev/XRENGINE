namespace XREngine.Rendering.Shadows;

public sealed partial class ShadowAtlasManager
{
    private readonly record struct ShadowBlock(int X, int Y, int Size)
    {
        public bool Contains(int x, int y, int size)
            => x >= X && y >= Y && x + size <= X + Size && y + size <= Y + Size;
    }
}
