namespace XREngine.Core.Reflection.Attributes
{
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public sealed class StringOptionsProviderAttribute : Attribute
    {
        public Type? ProviderType { get; }
        public string MethodName { get; }

        public StringOptionsProviderAttribute(string methodName)
        {
            MethodName = methodName;
        }

        public StringOptionsProviderAttribute(Type providerType, string methodName)
        {
            ProviderType = providerType;
            MethodName = methodName;
        }
    }
}
