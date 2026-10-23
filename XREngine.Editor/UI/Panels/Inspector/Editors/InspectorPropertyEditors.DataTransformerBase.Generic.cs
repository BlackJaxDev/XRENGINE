namespace XREngine.Editor;

public static partial class InspectorPropertyEditors
{
    /// <summary>Converts a cached common source value into retained editor fields.</summary>
    public abstract class DataTransformerBase<T> : DataTransformerBase
    {
        protected override void DisplaySourceValue(object? value)
            => DisplayValue(value is T typed ? typed : default);

        protected abstract bool Equal(T lhs, T rhs);
        protected abstract void DisplayValue(T? value);
    }
}
