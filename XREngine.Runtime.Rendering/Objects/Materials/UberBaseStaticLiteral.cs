using System.Buffers.Binary;
using System.Globalization;

namespace XREngine.Rendering;

/// <summary>Parses the finite scalar/vector literals emitted by the canonical Uber variant builder.</summary>
public static class UberBaseStaticLiteral
{
    /// <summary>Rejects expressions outside the admitted literal grammar instead of evaluating shader code on the host.</summary>
    public static bool TryWrite(string physicalType, string literal, Span<byte> destination)
    {
        ReadOnlySpan<char> text = literal.AsSpan().Trim();
        if (physicalType == "i32")
        {
            if (destination.Length != 4 || !int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value)) return false;
            BinaryPrimitives.WriteInt32LittleEndian(destination, value);
            return true;
        }
        int count = physicalType switch { "f32" => 1, "vec2<f32>" => 2, "vec4<f32>" => 4, _ => 0 };
        if (count == 0 || destination.Length != count * 4) return false;
        if (count > 1)
        {
            ReadOnlySpan<char> constructor = count == 2 ? "vec2" : "vec4";
            if (!text.StartsWith(constructor, StringComparison.Ordinal)) return false;
            text = text[constructor.Length..].Trim();
            if (text.Length < 3 || text[0] != '(' || text[^1] != ')') return false;
            text = text[1..^1].Trim();
        }
        Span<float> values = stackalloc float[4];
        int components = 0;
        while (!text.IsEmpty)
        {
            if (components >= count) return false;
            int separator = text.IndexOf(',');
            ReadOnlySpan<char> component = (separator < 0 ? text : text[..separator]).Trim();
            if (!float.TryParse(component, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value)) return false;
            values[components++] = value;
            if (separator < 0) { text = []; break; }
            text = text[(separator + 1)..].Trim();
            if (text.IsEmpty) return false;
        }
        if (components != 1 && components != count) return false;
        for (int index = 0; index < count; index++)
            BinaryPrimitives.WriteSingleLittleEndian(destination[(index * 4)..], values[components == 1 ? 0 : index]);
        return true;
    }

    /// <summary>Formats validated bytes as an invariant Slang literal for exact offline specialization.</summary>
    public static string FormatSlang(string physicalType, ReadOnlySpan<byte> value)
    {
        if (physicalType == "i32") return BinaryPrimitives.ReadInt32LittleEndian(value).ToString(CultureInfo.InvariantCulture);
        int count = physicalType switch { "f32" => 1, "vec2<f32>" => 2, "vec4<f32>" => 4, _ => throw new ArgumentException("Unsupported Uber literal type.", nameof(physicalType)) };
        // Each return is cold offline preparation; runtime encoding never formats text.
        string x = FormatFloat(BinaryPrimitives.ReadSingleLittleEndian(value));
        if (count == 1) return x;
        string y = FormatFloat(BinaryPrimitives.ReadSingleLittleEndian(value[4..]));
        if (count == 2) return $"float2({x}, {y})";
        string z = FormatFloat(BinaryPrimitives.ReadSingleLittleEndian(value[8..]));
        string w = FormatFloat(BinaryPrimitives.ReadSingleLittleEndian(value[12..]));
        return $"float4({x}, {y}, {z}, {w})";
    }

    private static string FormatFloat(float value)
    {
        if (!float.IsFinite(value)) throw new ArgumentException("Uber static values must be finite.", nameof(value));
        string text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.IndexOfAny(['.', 'e', 'E']) >= 0 ? text : text + ".0";
    }
}
