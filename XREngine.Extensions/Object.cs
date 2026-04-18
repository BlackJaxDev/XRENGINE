using System.Reflection;

namespace XREngine.Extensions
{
    public static class ObjectExtensions
    {
        public static object? CallPrivateMethod(this object o, string methodName, params object[] args)
            => o?.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(o, args);
        public static bool Is<T>(this object o)
            => o is T;
        public static bool IsNot<T>(this object o)
            => !(o is T);
        public static bool IsNull(this object o)
             => o is null;
        public static bool IsNotNull(this object o)
            => !(o is null);
    }
}
