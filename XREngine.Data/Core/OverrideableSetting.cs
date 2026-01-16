using MemoryPack;
using System.ComponentModel;

namespace XREngine.Data.Core
{
    /// <summary>
    /// Represents a setting that can optionally override a default value.
    /// Used in the cascading settings system where User > Project > Engine.
    /// </summary>
    /// <typeparam name="T">The type of the setting value.</typeparam>
    [Serializable]
    [MemoryPackable(GenerateType.NoGenerate)]
    public partial class OverrideableSetting<T> : XRBase, IMemoryPackable<OverrideableSetting<T>>, IOverrideableSetting
    {
        private bool _hasOverride = false;
        private T? _value;

        /// <summary>
        /// Creates an empty overrideable setting with no override applied.
        /// </summary>
        public OverrideableSetting()
        {
            _hasOverride = false;
            _value = default;
        }

        /// <summary>
        /// Creates an overrideable setting with the specified value and override state.
        /// </summary>
        /// <param name="value">The override value.</param>
        /// <param name="hasOverride">Whether the override is active.</param>
        public OverrideableSetting(T? value, bool hasOverride = true)
        {
            _value = value;
            _hasOverride = hasOverride;
        }

        /// <summary>
        /// Whether this setting has an active override.
        /// When false, the default/fallback value should be used.
        /// </summary>
        [Description("Whether this setting overrides the default value.")]
        public bool HasOverride
        {
            get => _hasOverride;
            set => SetField(ref _hasOverride, value);
        }

        /// <summary>
        /// The override value. Only meaningful when HasOverride is true.
        /// </summary>
        [Description("The override value for this setting.")]
        public T? Value
        {
            get => _value;
            set
            {
                if (SetField(ref _value, value))
                {
                    // Automatically enable override when a value is explicitly set
                    if (value is not null && !_hasOverride)
                        HasOverride = true;
                }
            }
        }

        /// <summary>
        /// Sets the value and enables the override.
        /// </summary>
        public void SetOverride(T value)
        {
            _value = value;
            HasOverride = true;
        }

        /// <summary>
        /// Clears the override, causing fallback to the next level's value.
        /// </summary>
        public void ClearOverride()
        {
            HasOverride = false;
            _value = default;
        }

        /// <summary>
        /// Resolves the effective value, using this override if active, otherwise returning the fallback.
        /// </summary>
        /// <param name="fallback">The value to use if this setting has no override.</param>
        /// <returns>The effective value.</returns>
        public T Resolve(T fallback)
            => HasOverride && _value is not null ? _value : fallback;

        /// <summary>
        /// Resolves the effective value for nullable value types.
        /// </summary>
        public T? ResolveNullable(T? fallback)
            => HasOverride ? _value : fallback;

        Type IOverrideableSetting.ValueType => typeof(T);

        object? IOverrideableSetting.BoxedValue
        {
            get => _value;
            set => Value = value is null ? default : (T?)value;
        }

        /// <summary>
        /// Implicit conversion from T to OverrideableSetting{T}.
        /// Creates an active override with the specified value.
        /// </summary>
        public static implicit operator OverrideableSetting<T>(T value)
            => new(value, true);

        /// <summary>
        /// Returns the override value or default if no override is set.
        /// Note: Use Resolve() for proper fallback handling.
        /// </summary>
        public static implicit operator T?(OverrideableSetting<T>? setting)
            => setting is not null && setting.HasOverride ? setting.Value : default;

        public override string ToString()
            => HasOverride ? $"Override: {_value}" : "No Override";

        #region MemoryPack Serialization

        static void IMemoryPackFormatterRegister.RegisterFormatter() { }

        static void IMemoryPackable<OverrideableSetting<T>>.Serialize<TBufferWriter>(
            ref MemoryPackWriter<TBufferWriter> writer,
            scoped ref OverrideableSetting<T>? value)
        {
            if (value is null)
            {
                writer.WriteNullObjectHeader();
                return;
            }

            writer.WriteObjectHeader(2);
            writer.WriteUnmanaged(value._hasOverride);
            writer.WriteValue(value._value);
        }

        static void IMemoryPackable<OverrideableSetting<T>>.Deserialize(
            ref MemoryPackReader reader,
            scoped ref OverrideableSetting<T>? value)
        {
            if (!reader.TryReadObjectHeader(out var memberCount))
            {
                value = null;
                return;
            }

            bool hasOverride = false;
            T? settingValue = default;

            if (memberCount >= 1)
                hasOverride = reader.ReadUnmanaged<bool>();
            if (memberCount >= 2)
                settingValue = reader.ReadValue<T>();

            value = new OverrideableSetting<T>(settingValue, hasOverride);
        }

        #endregion
    }

    /// <summary>
    /// Extension methods for working with OverrideableSetting values.
    /// </summary>
    public static class OverrideableSettingExtensions
    {
        /// <summary>
        /// Resolves a cascading chain of overrideable settings.
        /// Checks each level in order and returns the first active override, or the engine default.
        /// </summary>
        /// <typeparam name="T">The setting value type.</typeparam>
        /// <param name="engineDefault">The engine-level default value.</param>
        /// <param name="projectOverride">Optional project-level override.</param>
        /// <param name="userOverride">Optional user-level override.</param>
        /// <returns>The effective value after resolving the cascade.</returns>
        public static T ResolveCascade<T>(
            T engineDefault,
            OverrideableSetting<T>? projectOverride,
            OverrideableSetting<T>? userOverride)
        {
            // User overrides project overrides engine
            if (userOverride is not null && userOverride.HasOverride && userOverride.Value is T userVal)
                return userVal;

            if (projectOverride is not null && projectOverride.HasOverride && projectOverride.Value is T projectVal)
                return projectVal;

            return engineDefault;
        }

        /// <summary>
        /// Resolves a cascading chain for nullable value types.
        /// </summary>
        public static T? ResolveCascadeNullable<T>(
            T? engineDefault,
            OverrideableSetting<T>? projectOverride,
            OverrideableSetting<T>? userOverride) where T : struct
        {
            if (userOverride is not null && userOverride.HasOverride)
                return userOverride.Value;

            if (projectOverride is not null && projectOverride.HasOverride)
                return projectOverride.Value;

            return engineDefault;
        }
    }

    public interface IOverrideableSetting
    {
        bool HasOverride { get; set; }
        Type ValueType { get; }
        object? BoxedValue { get; set; }
    }
}
