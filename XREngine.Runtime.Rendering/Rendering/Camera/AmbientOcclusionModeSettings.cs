using System.Runtime.CompilerServices;

namespace XREngine.Rendering
{
    public abstract class AmbientOcclusionModeSettings
    {
        private readonly string _propertyPrefix;

        protected AmbientOcclusionModeSettings(AmbientOcclusionSettings owner, string propertyPrefix)
        {
            Owner = owner;
            _propertyPrefix = propertyPrefix;
        }

        protected AmbientOcclusionSettings Owner { get; }

        protected bool SetValue<T>(ref T field, T value, string? compatibilityPropertyName = null, [CallerMemberName] string? propertyName = null)
            => Owner.SetNestedField(ref field, value, BuildPath(propertyName), compatibilityPropertyName);

        protected string BuildPath(string? propertyName)
            => $"{_propertyPrefix}.{propertyName}";

        protected static float PositiveOr(float value, float fallback)
            => value > 0.0f ? value : fallback;

        protected static int PositiveOr(int value, int fallback)
            => value > 0 ? value : fallback;

        public abstract void ApplyUniforms(XRRenderProgram program);
    }
}