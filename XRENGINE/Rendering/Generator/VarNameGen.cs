namespace XREngine.Rendering.Shaders.Generator
{
    public class VarNameGen
    {
        private int _generatedNameCount = 0;
        private string _selection = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLNNOPQRSTUVWXYZ";

        public string New()
        {
            int digitIndex = _generatedNameCount % _selection.Length;
            int digitCount = _generatedNameCount / _selection.Length;
            string s = "";
            for (int i = 0; i <= digitCount; ++i)
                s += _selection[digitIndex];
            ++_generatedNameCount;
            return s;
        }
    }
}
