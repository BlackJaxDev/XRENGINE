using XREngine.Core.Tools;

namespace XREngine.Core.Reflection.Attributes
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public class EditorCallableIf(string condition) : Attribute
    {
        public bool Evaluate(object owningObject) 
            => ExpressionParser.Evaluate<bool>(condition, owningObject);
    }
}