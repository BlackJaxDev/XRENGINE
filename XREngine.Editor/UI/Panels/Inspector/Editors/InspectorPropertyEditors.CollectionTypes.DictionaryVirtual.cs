using System.Reflection;
using XREngine.Editor.UI;
using XREngine.Scene;

namespace XREngine.Editor;

public static partial class InspectorPropertyEditors
{
    public static partial class CollectionTypes
    {
        private sealed partial class CollectionEditorContext
        {
            private void RenderVirtualDictionary(Type keyType, Type valueType,
                EditorDictionarySourceLease[] sources, EditorDictionarySnapshot primary,
                ulong generation)
            {
                Type bindingType = typeof(DictionaryValueBinding<,>).MakeGenericType(keyType, valueType);
                PropertyInfo bindingProperty = bindingType.GetProperty(
                    nameof(DictionaryValueBinding<object, object>.Value))!;
                EditorFixedHeightRowContract contract = new(
                    valueType,
                    bindingProperty,
                    primary.Keys.Length,
                    "Add Entry",
                    index => primary.Keys[index].ToString() ?? "<null>",
                    (index, rowRead, rowWrite) => CreateDictionaryBindings(bindingType,
                        primary.Keys[index], generation, sources, rowRead, rowWrite),
                    (index, rowWrite) => RemoveDictionaryEntry(
                        CreateDictionaryRowSources(sources, rowWrite), primary.Keys[index], generation),
                    () => AddDictionaryEntry(sources, generation));
                RenderVirtualFixedHeightRows(contract, generation);
            }

            private static EditorDictionarySourceLease[] CreateDictionaryRowSources(
                EditorDictionarySourceLease[] sources, Func<bool> rowIsCurrent)
            {
                EditorDictionarySourceLease[] rows = new EditorDictionarySourceLease[sources.Length];
                for (int index = 0; index < sources.Length; index++)
                {
                    EditorDictionarySourceLease source = sources[index];
                    rows[index] = new()
                    {
                        Owner = source.Owner,
                        Property = source.Property,
                        Dictionary = source.Dictionary,
                        Adapter = source.Adapter,
                        Accessor = source.Accessor,
                        Displayed = source.Displayed,
                        State = source.State,
                        World = source.World,
                        Document = source.Document,
                        SceneNode = source.SceneNode,
                        IsViewCurrent = rowIsCurrent,
                    };
                }
                return rows;
            }
        }
    }
}
