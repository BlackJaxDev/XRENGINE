namespace XREngine.Core.Reflection.Attributes
{
    [Serializable]
    public class TStringAttribute : Attribute
    {
        public bool MultiLine { get; set; }
        public bool Path { get; set; }
        public bool Unicode { get; set; }
        public bool Nullable { get; set; }

        public TStringAttribute(bool multiLine = false, bool path = false, bool unicode = false, bool nullable = true)
        {
            MultiLine = multiLine;
            Path = path;
            Unicode = unicode;
            Nullable = nullable;
        }
    }
}
