namespace XREngine.Core.Attributes
{
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public class TNumericPrefixSuffixAttribute : Attribute
    {
        public string Prefix { get; }
        public string Suffix { get; }
        public TNumericPrefixSuffixAttribute(string prefix, string suffix)
        {
            Prefix = prefix;
            Suffix = suffix;
        }
    }
}
