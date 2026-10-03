namespace XREngine.Tools.ShaderCooker;

internal sealed partial class WgslAbiParser
{
    private readonly HashSet<string> _privateVariables = new(StringComparer.Ordinal);

    /// <summary>
    /// Invocation-local scalar/vector accumulators have no resource binding or
    /// host layout. Keep them distinct from bound resources and workgroup storage.
    /// </summary>
    private void PrivateDeclaration(List<WgslAbiAttribute> attributes)
    {
        if (attributes.Count != 0 || _privateVariables.Count >= 32)
            Fail(Current.Offset, "private accumulators require unbound declarations within the 32-variable bound");
        Expect("<");
        Expect("private");
        Expect(">");
        WgslAbiToken name = Identifier();
        Expect(":");
        string type = TypeUntil(";");
        Expect(";");
        bool scalar = type is "bool" or "f32" or "i32" or "u32";
        bool vector = type is "vec2f" or "vec3f" or "vec4f" or
            "vec2<f32>" or "vec3<f32>" or "vec4<f32>" or
            "vec2<i32>" or "vec3<i32>" or "vec4<i32>" or
            "vec2<u32>" or "vec3<u32>" or "vec4<u32>" or
            "vec2<bool>" or "vec3<bool>" or "vec4<bool>";
        if (!scalar && !vector)
            Fail(name.Offset, "private accumulators require explicit scalar/vector types without initializers");
        if (!_privateVariables.Add(name.Text))
            Fail(name.Offset, "duplicate private accumulator name");
    }
}
