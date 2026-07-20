using XREngine.Core.Tools;

namespace XREngine.Core.Reflection.Attributes
{
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public class EditorBrowsableIf(string condition) : Attribute
    {
        public bool Evaluate(object owningObject) 
            => ExpressionParser.Evaluate<bool>(condition, owningObject);
    }
}