using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using XREngine.Components.Scene.Mesh;
using XREngine.Editor.ComponentEditors;
using XREngine.Rendering;

namespace XREngine.Editor;

/// <summary>
/// A property object for one blendshape weight of a <see cref="ModelComponent"/>, in percent. The native
/// inspector keys and binds rows by property object, so each blendshape name gets one wrapper, and its row edits
/// the weight with the float editor. This is the native form of the "Blendshapes (All LODs)" table of the ImGui
/// model editor: the value is the mean weight of all mesh renderers that have the blendshape, and a write sets
/// the weight on all of them and pushes the weights to the GPU.
/// </summary>
internal sealed class InspectorBlendshapePropertyInfo : PropertyInfo
{
    private static readonly ConcurrentDictionary<string, InspectorBlendshapePropertyInfo> Wrappers =
        new(StringComparer.InvariantCultureIgnoreCase);

    private InspectorBlendshapePropertyInfo(string blendshapeName) => BlendshapeName = blendshapeName;

    /// <summary>The blendshape name, which the model component uses to find the shape in each mesh.</summary>
    public string BlendshapeName { get; }

    /// <summary>The title of the group row that starts the blendshape rows of a model section.</summary>
    public const string GroupTitle = "Blendshapes";

    /// <summary>Returns the wrapper of <paramref name="blendshapeName"/>. The same name returns the same wrapper.</summary>
    public static InspectorBlendshapePropertyInfo For(string blendshapeName)
        => Wrappers.GetOrAdd(blendshapeName, static name => new InspectorBlendshapePropertyInfo(name));

    /// <summary>
    /// Adds one wrapper for each distinct blendshape of the runtime meshes of <paramref name="model"/>, in name
    /// order. Like the ImGui editor, shapes that move no vertex are left out.
    /// </summary>
    public static void AddRows(ModelComponent model, List<PropertyInfo> members)
    {
        HashSet<string> effective = new(StringComparer.InvariantCultureIgnoreCase);
        ModelComponentEditor.RefreshEffectiveBlendshapeNames(model, effective);
        if (effective.Count == 0)
            return;
        string[] names = [.. effective];
        Array.Sort(names, StringComparer.InvariantCultureIgnoreCase);
        foreach (string name in names)
            members.Add(For(name));
    }

    public override string Name => BlendshapeName;
    public override Type PropertyType => typeof(float);
    public override PropertyAttributes Attributes => PropertyAttributes.None;
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override Type? DeclaringType => typeof(ModelComponent);
    public override Type? ReflectedType => typeof(ModelComponent);
    public override Module Module => typeof(ModelComponent).Module;
    public override MethodInfo[] GetAccessors(bool nonPublic) => [];
    public override MethodInfo? GetGetMethod(bool nonPublic) => null;
    public override MethodInfo? GetSetMethod(bool nonPublic) => null;
    public override ParameterInfo[] GetIndexParameters() => [];

    /// <summary>Returns the mean weight, in percent, of the renderers of <paramref name="obj"/> that have the shape.</summary>
    public override object? GetValue(object? obj, BindingFlags invokeAttr, Binder? binder, object?[]? index, CultureInfo? culture)
    {
        if (obj is not ModelComponent model)
            return 0.0f;
        float sum = 0.0f;
        int count = 0;
        foreach (RenderableMesh mesh in model.Meshes)
            foreach (var lod in mesh.LODs)
            {
                if (lod.Renderer is not XRMeshRenderer { Mesh: { HasBlendshapes: true } data } renderer)
                    continue;
                string[] names = data.BlendshapeNames;
                for (int i = 0; i < names.Length; i++)
                {
                    if (!string.Equals(names[i], BlendshapeName, StringComparison.InvariantCultureIgnoreCase))
                        continue;
                    sum += renderer.GetBlendshapeWeight((uint)i);
                    count++;
                    break;
                }
            }
        return count == 0 ? 0.0f : sum / count;
    }

    /// <summary>Sets the weight, in percent from 0 to 100, on every renderer of the model that has the shape.</summary>
    public override void SetValue(object? obj, object? value, BindingFlags invokeAttr, Binder? binder, object?[]? index, CultureInfo? culture)
    {
        if (obj is not ModelComponent model || value is null)
            return;
        float percent = Math.Clamp(Convert.ToSingle(value, CultureInfo.InvariantCulture), 0.0f, 100.0f);
        model.SetBlendShapeWeight(BlendshapeName, percent);
        ModelComponentEditor.PushBlendshapeWeightsToGpu(model, BlendshapeName);
    }

    // Attribute lookups cast the result to an array of the requested attribute type, so the empty arrays are typed.
    public override object[] GetCustomAttributes(bool inherit) => Array.Empty<Attribute>();
    public override object[] GetCustomAttributes(Type attributeType, bool inherit)
        => (object[])Array.CreateInstance(attributeType, 0);
    public override bool IsDefined(Type attributeType, bool inherit) => false;
    public override IList<CustomAttributeData> GetCustomAttributesData() => [];
}
