using System.Reflection;

namespace XREngine.Animation
{
    /// <summary>
    /// Base class for animations that animate properties such as Vector3, bool and float.
    /// </summary>
    public abstract class BasePropAnim(float lengthInSeconds, bool looped) : BaseAnimation(lengthInSeconds, looped)
    {
        public const string PropAnimCategory = "Property Animation";

        /// <summary>
        /// Call to set this animation's current value to an object's property and then advance the animation by the given delta.
        /// </summary>
        public object? SetAndTick(object obj, FieldInfo field, float delta)
        {
            var value = SetValue(obj, field);
            Tick(delta);
            return value;
        }

        public object? SetValue(object obj, FieldInfo field)
        {
            var value = GetCurrentValueGeneric();
            field.SetValue(obj, value);
            return value;
        }

        /// <summary>
        /// Call to set this animation's current value to an object's property and then advance the animation by the given delta.
        /// </summary>
        public object? SetAndTick(object obj, PropertyInfo property, float delta)
        {
            var value = SetValue(obj, property);
            Tick(delta);
            return value;
        }

        public object? SetValue(object obj, PropertyInfo property)
        {
            var value = GetCurrentValueGeneric();
            property.SetValue(obj, value);
            return value;
        }

        /// <summary>
        /// Call to set this animation's current value to an object's method that takes it as a single argument and then advance the animation by the given delta.
        /// </summary>
        public object? SetAndTick(object obj, MethodInfo method, float delta, int valueArgumentIndex, object?[] methodArguments)
        {
            object? value = SetValue(obj, method, valueArgumentIndex, methodArguments);
            Tick(delta);
            return value;
        }

        public object? SetValue(object obj, MethodInfo method, int valueArgumentIndex, object?[] methodArguments)
        {
            methodArguments[valueArgumentIndex] = GetCurrentValueGeneric();
            return method.Invoke(obj, methodArguments);
        }

        /// <summary>
        /// Retrieves the value for the animation's current time.
        /// Used by the internal animation implementation to set property/field values and call methods,
        /// so must be overridden.
        /// </summary>
        protected abstract object? GetCurrentValueGeneric();
        /// <summary>
        /// Retrieves the value for the given second.
        /// Used by the internal animation implementation to set property/field values and call methods,
        /// so must be overridden.
        /// </summary>
        protected abstract object? GetValueGeneric(float second);
    }
}