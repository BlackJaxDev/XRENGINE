using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace XREngine.Editor;

public static class ImGuiEditorUtilities
{
    public sealed class CollectionEditorAdapter
    {
        private IList _list;
        private readonly Type _elementType;
        private readonly Func<List<object?>, IList?>? _replacementHandler;

        private CollectionEditorAdapter(IList list, Type elementType, Func<List<object?>, IList?>? replacementHandler)
        {
            _list = list ?? throw new ArgumentNullException(nameof(list));
            _elementType = elementType ?? typeof(object);
            _replacementHandler = replacementHandler;
        }

        public static CollectionEditorAdapter ForList(IList list, Type elementType)
            => new(list, elementType, null);

        public static CollectionEditorAdapter ForArray(Array array, Type elementType, Func<Array, bool>? applyReplacement)
        {
            if (applyReplacement is null)
                return new CollectionEditorAdapter(array, elementType, null);

            IList? ReplacementFactory(List<object?> values)
            {
                Array replacement = Array.CreateInstance(elementType, values.Count);
                for (int i = 0; i < values.Count; i++)
                {
                    object? converted = ConvertElement(elementType, values[i]);
                    replacement.SetValue(converted, i);
                }

                return applyReplacement(replacement) ? (IList)replacement : null;
            }

            return new CollectionEditorAdapter(array, elementType, ReplacementFactory);
        }

        public IList Items => _list;
        public Type ElementType => _elementType;
        public int Count => _list.Count;
        public bool CanModifyInPlace => !_list.IsReadOnly && !_list.IsFixedSize;
        public bool CanAddRemove => CanModifyInPlace || _replacementHandler is not null;

        public bool TryAdd(object? value)
        {
            value = ConvertElement(_elementType, value);

            if (CanModifyInPlace)
            {
                _list.Add(value);
                return true;
            }

            if (_replacementHandler is null)
                return false;

            var values = CopyValues();
            values.Add(value);
            return Replace(values);
        }

        public bool TryInsert(int index, object? value)
        {
            value = ConvertElement(_elementType, value);

            if (CanModifyInPlace)
            {
                if (index < 0)
                    index = 0;
                else if (index > _list.Count)
                    index = _list.Count;
                _list.Insert(index, value);
                return true;
            }

            if (_replacementHandler is null)
                return false;

            var values = CopyValues();
            if (index < 0)
                index = 0;
            else if (index > values.Count)
                index = values.Count;
            values.Insert(index, value);
            return Replace(values);
        }

        public bool TryRemoveAt(int index)
        {
            if (index < 0 || index >= Count)
                return false;

            if (CanModifyInPlace)
            {
                _list.RemoveAt(index);
                return true;
            }

            if (_replacementHandler is null)
                return false;

            var values = CopyValues();
            values.RemoveAt(index);
            return Replace(values);
        }

        public bool TryReplace(int index, object? value)
        {
            if (index < 0 || index >= Count)
                return false;

            value = ConvertElement(_elementType, value);

            if (CanModifyInPlace)
            {
                if (Equals(_list[index], value))
                    return false;

                _list[index] = value!;
                return true;
            }

            if (_replacementHandler is null)
                return false;

            var values = CopyValues();
            if (Equals(values[index], value))
                return false;

            values[index] = value;
            return Replace(values);
        }

        public object? CreateDefaultElement()
            => ImGuiEditorUtilities.CreateDefaultElement(_elementType);

        private List<object?> CopyValues()
        {
            var values = new List<object?>(_list.Count);
            foreach (var item in _list)
                values.Add(item);
            return values;
        }

        private bool Replace(List<object?> values)
        {
            if (_replacementHandler is null)
                return false;

            IList? replacement = _replacementHandler(values);
            if (replacement is null)
                return false;

            _list = replacement;
            return true;
        }
    }

    public static object? CreateDefaultElement(Type elementType)
    {
        if (elementType == typeof(string))
            return string.Empty;

        if (elementType == typeof(bool))
            return false;

        if (elementType == typeof(Vector2))
            return Vector2.Zero;

        if (elementType == typeof(Vector3))
            return Vector3.Zero;

        if (elementType == typeof(Vector4))
            return Vector4.Zero;

        if (elementType.IsEnum)
        {
            Array enumValues = Enum.GetValues(elementType);
            return enumValues.Length > 0 ? enumValues.GetValue(0) : Activator.CreateInstance(elementType);
        }

        Type? nullableUnderlying = Nullable.GetUnderlyingType(elementType);
        if (nullableUnderlying is not null)
            return null;

        if (elementType.IsValueType)
            return Activator.CreateInstance(elementType);

        if (elementType.GetConstructor(Type.EmptyTypes) is not null)
            return Activator.CreateInstance(elementType);

        return null;
    }

    private static object? ConvertElement(Type elementType, object? value)
    {
        if (value is null)
        {
            if (IsNonNullableValueType(elementType))
                return Activator.CreateInstance(elementType);
            return null;
        }

        if (elementType.IsInstanceOfType(value))
            return value;

        try
        {
            return Convert.ChangeType(value, elementType, CultureInfo.InvariantCulture);
        }
        catch
        {
            return elementType.IsValueType ? Activator.CreateInstance(elementType) : null;
        }
    }

    private static bool IsNonNullableValueType(Type type)
        => type.IsValueType && Nullable.GetUnderlyingType(type) is null;
}
