namespace XREngine.Core.Reflection.Attributes
{
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public class TReplicate : Attribute
    {
        public string EventMethodName { get; set; } = string.Empty;

        public TReplicate() { }
        public TReplicate(string eventMethodName) 
            => EventMethodName = eventMethodName;
    }
}
