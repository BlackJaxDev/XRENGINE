using XREngine;

namespace Extensions
{
    public static class StringExtension
    {
        public static T? ParseAs<T>(this string value)
            => (T?)value.ParseAs(typeof(T));
        public static object? ParseAs(this string value, Type t)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                if (string.IsNullOrWhiteSpace(value))
                    return null;
                else
                    return value.ParseAs(t.GetGenericArguments()[0]);
            }
            if (t.GetInterface(nameof(ISerializableString)) != null)
            {
                if (Activator.CreateInstance(t) is ISerializableString o)
                {
                    o.ReadFromString(value);
                    return o;
                }
                return null;
            }
            if (t.BaseType is not null && string.Equals(t.BaseType.Name, "Enum", StringComparison.InvariantCulture))
                return Enum.Parse(t, value);
            return t.Name switch
            {
                "Boolean" => Boolean.Parse(value),
                "SByte" => SByte.Parse(value),
                "Byte" => Byte.Parse(value),
                "Char" => Char.Parse(value),
                "Int16" => Int16.Parse(value),
                "UInt16" => UInt16.Parse(value),
                "Int32" => Int32.Parse(value),
                "UInt32" => UInt32.Parse(value),
                "Int64" => Int64.Parse(value),
                "UInt64" => UInt64.Parse(value),
                "Single" => Single.Parse(value),
                "Double" => Double.Parse(value),
                "Decimal" => Decimal.Parse(value),
                "String" => value,
                _ => throw new InvalidOperationException(t.ToString() + " is not parsable"),
            };
        }
    }
}
