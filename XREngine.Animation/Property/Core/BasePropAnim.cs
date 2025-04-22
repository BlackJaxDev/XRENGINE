using ImmediateReflection;
using System.Numerics;
using System.Reflection;
using XREngine.Data.Colors;

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
        public object? SetAndTick(object obj, FieldInfo field, float delta, float valueApplyWeight)
        {
            var value = SetValue(obj, field, valueApplyWeight);
            Tick(delta);
            return value;
        }

        public object? SetValue(object obj, FieldInfo field, float valueApplyWeight)
        {
            object? value = GetCurrentValueGeneric();
            ScaleValue(ref value, valueApplyWeight);
            field.SetValue(obj, value);
            return value;
        }

        /// <summary>
        /// Call to set this animation's current value to an object's property and then advance the animation by the given delta.
        /// </summary>
        public object? SetAndTick(object obj, ImmediateField field, float delta, float valueApplyWeight)
        {
            var value = SetValue(obj, field, valueApplyWeight);
            Tick(delta);
            return value;
        }

        public object? SetValue(object obj, ImmediateField field, float valueApplyWeight)
        {
            object? value = GetCurrentValueGeneric();
            ScaleValue(ref value, valueApplyWeight);
            field.SetValue(obj, value);
            return value;
        }

        /// <summary>
        /// Call to set this animation's current value to an object's property and then advance the animation by the given delta.
        /// </summary>
        public object? SetAndTick(object obj, PropertyInfo property, float delta, float valueApplyWeight)
        {
            var value = SetValue(obj, property, valueApplyWeight);
            Tick(delta);
            return value;
        }

        public object? SetValue(object obj, PropertyInfo property, float valueApplyWeight)
        {
            object? value = GetCurrentValueGeneric();
            ScaleValue(ref value, valueApplyWeight);
            property.SetValue(obj, value);
            return value;
        }

        /// <summary>
        /// Call to set this animation's current value to an object's property and then advance the animation by the given delta.
        /// </summary>
        public object? SetAndTick(object obj, ImmediateProperty property, float delta, float valueApplyWeight)
        {
            var value = SetValue(obj, property, valueApplyWeight);
            Tick(delta);
            return value;
        }

        public object? SetValue(object obj, ImmediateProperty property, float valueApplyWeight)
        {
            object? value = GetCurrentValueGeneric();
            ScaleValue(ref value, valueApplyWeight);
            property.SetValue(obj, value);
            return value;
        }

        /// <summary>
        /// Call to set this animation's current value to an object's method that takes it as a single argument and then advance the animation by the given delta.
        /// </summary>
        public object? SetAndTick(object obj, MethodInfo method, float delta, int valueArgumentIndex, object?[] methodArguments, float valueApplyWeight)
        {
            object? value = SetValue(obj, method, valueArgumentIndex, methodArguments, valueApplyWeight);
            Tick(delta);
            return value;
        }

        public object? SetValue(object obj, MethodInfo method, int valueArgumentIndex, object?[] methodArguments, float valueApplyWeight)
        {
            methodArguments[valueArgumentIndex] = GetCurrentValueGeneric();
            ScaleValue(ref methodArguments[valueArgumentIndex], valueApplyWeight);
            return method.Invoke(obj, methodArguments);
        }

        private static void ScaleValue(ref object? v, float valueApplyWeight)
        {
            switch (v)
            {
                case float f:
                    v = f * valueApplyWeight;
                    break;
                case Vector2 vec2:
                    v = vec2 * valueApplyWeight;
                    break;
                case Vector3 vec3:
                    v = vec3 * valueApplyWeight;
                    break;
                case Vector4 vec4:
                    v = vec4 * valueApplyWeight;
                    break;
                case Quaternion quat:
                    v = Quaternion.Slerp(Quaternion.Identity, quat, valueApplyWeight);
                    break;
                case ColorF3 color:
                    v = color * valueApplyWeight;
                    break;
                case ColorF4 color4:
                    v = color4 * valueApplyWeight;
                    break;
            }
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