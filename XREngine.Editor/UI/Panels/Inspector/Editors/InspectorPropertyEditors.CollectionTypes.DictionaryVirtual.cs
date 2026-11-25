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
                EditorDictionaryRowPlan plan, ulong generation)
            {
                Type bindingType = typeof(DictionaryValueBinding<,>).MakeGenericType(keyType, valueType);
                PropertyInfo bindingProperty = bindingType.GetProperty(
                    nameof(DictionaryValueBinding<object, object>.Value))!;
                EditorVirtualRowContract contract = new(
                    valueType,
                    bindingProperty,
                    primary.Keys.Length,
                    "Add Entry",
                    plan.Label,
                    (index, rowRead, rowWrite) => CreateDictionaryBindings(bindingType,
                        primary.Keys[index], generation, sources, rowRead, rowWrite),
                    (index, rowWrite) => RemoveDictionaryEntry(
                        CreateDictionaryRowSources(sources, rowWrite), primary.Keys[index], generation),
                    () => AddDictionaryEntry(sources, generation),
                    plan.ReadOnlyText,
                    CreateDictionaryRowExtent(plan, valueType, primary),
                    MeasuredRowEditorFor(valueType) is not null);
                RenderVirtualRows(contract, generation);
            }

            /// <summary>
            /// Measures each row from the retained line counts of the plan and from a custom value
            /// editor that measures its own rows. A row uses the larger of the two heights. Returns
            /// null when every row has the single-line height.
            /// </summary>
            private static Func<int, float>? CreateDictionaryRowExtent(EditorDictionaryRowPlan plan,
                Type valueType, EditorDictionarySnapshot primary)
            {
                float fontSize = EditorUI.Styles.PropertyNameFontSize;
                Func<int, float>? measured = CreateMeasuredRowExtent(valueType, index => primary.Values[index]);
                if (measured is null)
                    return plan.HasMultilineRows
                        ? index => EditorVirtualRowExtent.ForLines(plan.LineCount(index), fontSize)
                        : null;
                return index => MathF.Max(measured(index),
                    EditorVirtualRowExtent.ForLines(plan.LineCount(index), fontSize));
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
