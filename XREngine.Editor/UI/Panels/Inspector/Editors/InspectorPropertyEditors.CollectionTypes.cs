using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using XREngine;
using XREngine.Data.Colors;
using XREngine.Editor.UI;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

public static partial class InspectorPropertyEditors
{
    public static class CollectionTypes
    {
        private const float RowHeaderWidth = 150.0f;
        private const float ButtonHeight = 24.0f;
        private const float ButtonWidth = 80.0f;

        private static readonly Dictionary<Type, CollectionAccessor?> CollectionAccessorCache = new();

        public static bool TryCreateEditor(Type propType, out Action<SceneNode, PropertyInfo, object?[]?>? editor)
        {
            if (!IsSupportedCollectionType(propType))
            {
                editor = null;
                return false;
            }

            editor = (node, prop, objects) =>
            {
                EnsureVerticalLayout(node);
                var container = node.NewChild();
                EnsureVerticalLayout(container);
                var context = new CollectionEditorContext(prop, objects, propType, container);
                context.Rebuild();
            };
            return true;
        }

        private static bool IsSupportedCollectionType(Type type)
            => typeof(IDictionary).IsAssignableFrom(type)
            || ImplementsGenericInterface(type, typeof(IDictionary<,>))
            || typeof(IList).IsAssignableFrom(type)
            || ImplementsGenericInterface(type, typeof(IList<>))
            || IsLinkedListType(type)
            || typeof(ICollection).IsAssignableFrom(type)
            || ImplementsGenericInterface(type, typeof(ICollection<>));

        private sealed class CollectionEditorContext(PropertyInfo property, object?[]? objects, Type declaredType, SceneNode container)
        {
            private readonly PropertyInfo _property = property;
            private readonly List<object> _owners = CollectOwners(objects);
            private readonly Type _declaredType = declaredType;
            private readonly SceneNode _container = container;

            public void Rebuild()
            {
                _container.Transform.Clear();

                if (_owners.Count == 0)
                {
                    AddInfoLabel(_container, "No valid targets.");
                    return;
                }

                object? sample = GetSampleCollection();
                if (sample is null)
                {
                    AddInfoLabel(_container, "Collection is null.");
                    return;
                }

                if (sample is IDictionary dictionary)
                {
                    var (keyType, valueType) = ResolveDictionaryTypes(_declaredType, sample.GetType());
                    RenderDictionary(dictionary, keyType, valueType);
                    return;
                }

                if (IsLinkedListInstance(sample, out var linkedListElementType))
                {
                    RenderLinkedList(sample, linkedListElementType);
                    return;
                }

                if (sample is IList list)
                {
                    var elementType = ResolveElementType(_declaredType, sample.GetType());
                    RenderList(elementType);
                    return;
                }

                if (sample is ICollection collection)
                {
                    var elementType = ResolveElementType(_declaredType, sample.GetType());
                    RenderGenericCollection(collection, elementType);
                    return;
                }

                AddInfoLabel(_container, $"Unsupported collection type '{sample.GetType().Name}'.");
            }

            private object? GetSampleCollection()
            {
                foreach (var owner in _owners)
                {
                    try
                    {
                        var value = _property.GetValue(owner);
                        if (value is not null)
                            return value;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex, $"Failed to read property '{_property.Name}'.");
                    }
                }
                return null;
            }

            private void RenderDictionary(IDictionary template, Type keyType, Type valueType)
            {
                AddInfoLabel(_container, $"Count: {template.Count}");

                bool canMutate = HasWritableDictionary();
                var header = _container.NewChild();
                var headerLayout = header.SetTransform<UIListTransform>();
                headerLayout.DisplayHorizontal = true;
                headerLayout.ItemSpacing = 6.0f;

                if (canMutate)
                {
                    CreateInlineButton(header, "Add Entry", () =>
                    {
                        AddDictionaryEntry(keyType, valueType);
                    });
                }
                else
                {
                    AddInfoLabel(header, "Dictionary is read-only.");
                }

                var listNode = _container.NewChild();
                EnsureVerticalLayout(listNode);

                var keys = template.Keys.Cast<object?>().ToList();
                if (keys.Count == 0)
                {
                    AddInfoLabel(listNode, "<empty>");
                    return;
                }

                foreach (var key in keys)
                    BuildDictionaryRow(listNode, key, keyType, valueType, canMutate);
            }

            private void BuildDictionaryRow(SceneNode parent, object? key, Type keyType, Type valueType, bool canMutate)
            {
                var row = parent.NewChild();
                var splitter = row.SetTransform<UIDualSplitTransform>();
                splitter.VerticalSplit = false;
                splitter.FirstFixedSize = true;
                splitter.FixedSize = RowHeaderWidth;

                var labelNode = row.NewChild();
                var labelList = labelNode.SetTransform<UIListTransform>();
                labelList.DisplayHorizontal = true;
                labelList.ItemSpacing = 4.0f;

                labelNode.NewChild<UITextComponent>(out var label);
                label.Text = key?.ToString() ?? "<null>";
                label.FontSize = EditorUI.Styles.PropertyNameFontSize;
                label.HorizontalAlignment = EHorizontalAlignment.Left;
                label.VerticalAlignment = EVerticalAlignment.Center;
                label.Color = EditorUI.Styles.PropertyNameTextColor;
                label.BoundableTransform.Margins = new Vector4(4.0f, 2.0f, 4.0f, 2.0f);

                if (canMutate)
                {
                    CreateInlineButton(labelNode, "Remove", () => RemoveDictionaryEntry(key));
                }

                var valueNode = row.NewChild();
                var bindingType = typeof(DictionaryValueBinding<,>).MakeGenericType(keyType, valueType);
                var bindingProperty = bindingType.GetProperty(nameof(DictionaryValueBinding<object, object>.Value))!;
                var targets = CreateDictionaryBindings(bindingType, key);
                BuildValueEditor(valueNode, valueType, bindingProperty, targets);
            }

            private object[] CreateDictionaryBindings(Type bindingType, object? key)
            {
                List<object> bindings = new();
                foreach (var owner in _owners)
                {
                    if (_property.GetValue(owner) is not IDictionary dictionary)
                        continue;

                    if (key is not null && !dictionary.Contains(key))
                    {
                        if (!ContainsKeyByEquality(dictionary, key))
                            continue;
                    }
                    else if (key is null && !dictionary.Contains(null!))
                    {
                        continue;
                    }

                    var instance = Activator.CreateInstance(bindingType, _property, owner, key);
                    if (instance is not null)
                        bindings.Add(instance);
                }
                return bindings.ToArray();
            }

            private static bool ContainsKeyByEquality(IDictionary dictionary, object key)
            {
                foreach (var existingKey in dictionary.Keys)
                {
                    if (Equals(existingKey, key))
                        return true;
                }
                return false;
            }

            private void RemoveDictionaryEntry(object? key)
            {
                foreach (var owner in _owners)
                {
                    if (_property.GetValue(owner) is not IDictionary dictionary || dictionary.IsReadOnly)
                        continue;

                    try
                    {
                        if (key is null && dictionary.Contains(null!))
                            dictionary.Remove(null!);
                        else if (key is not null && dictionary.Contains(key))
                            dictionary.Remove(key);
                        else
                        {
                            RemoveByEquality(dictionary, key);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex, $"Failed to remove dictionary entry from '{_property.Name}'.");
                    }
                }

                Rebuild();
            }

            private static void RemoveByEquality(IDictionary dictionary, object? key)
            {
                object? removeKey = null;
                foreach (var existingKey in dictionary.Keys)
                {
                    if (Equals(existingKey, key))
                    {
                        removeKey = existingKey;
                        break;
                    }
                }

                if (removeKey is not null)
                    dictionary.Remove(removeKey);
            }

            private bool HasWritableDictionary()
            {
                foreach (var owner in _owners)
                {
                    if (_property.GetValue(owner) is IDictionary dictionary && !dictionary.IsReadOnly)
                        return true;
                }
                return false;
            }

            private void AddDictionaryEntry(Type keyType, Type valueType)
            {
                foreach (var owner in _owners)
                {
                    if (_property.GetValue(owner) is not IDictionary dictionary || dictionary.IsReadOnly)
                        continue;

                    object? baseKey = ImGuiEditorUtilities.CreateDefaultElement(keyType);
                    object? uniqueKey = EnsureUniqueKey(dictionary, baseKey, keyType);
                    if (uniqueKey is null)
                    {
                        Debug.LogWarning($"Unable to auto-generate a unique key for '{_property.Name}'.");
                        continue;
                    }

                    object? newValue = ImGuiEditorUtilities.CreateDefaultElement(valueType);
                    try
                    {
                        dictionary[uniqueKey] = newValue;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex, $"Failed to add dictionary entry to '{_property.Name}'.");
                    }
                }

                Rebuild();
            }

            private static object? EnsureUniqueKey(IDictionary dictionary, object? baseKey, Type keyType)
            {
                if (baseKey is null)
                    return null;

                if (!dictionary.Contains(baseKey))
                    return baseKey;

                if (keyType == typeof(string))
                {
                    string root = string.IsNullOrWhiteSpace(baseKey as string) ? "Key" : (string)baseKey;
                    int index = 1;
                    string candidate;
                    do
                    {
                        candidate = $"{root}_{index++}";
                    } while (dictionary.Contains(candidate));
                    return candidate;
                }

                if (IsNumericType(keyType))
                {
                    long attempt = 0;
                    if (baseKey is IConvertible convertible)
                        attempt = convertible.ToInt64(null);

                    object? candidate;
                    do
                    {
                        candidate = ConvertValue(attempt++, keyType);
                    }
                    while (candidate is not null && dictionary.Contains(candidate));
                    return candidate;
                }

                return null;
            }

            private void RenderList(Type elementType)
            {
                int? sampleCount = GetListSample()?.Count;
                if (sampleCount is int count)
                    AddInfoLabel(_container, $"Count: {count}");

                bool canMutate = HasMutableList();
                var header = _container.NewChild();
                var headerLayout = header.SetTransform<UIListTransform>();
                headerLayout.DisplayHorizontal = true;
                headerLayout.ItemSpacing = 6.0f;

                if (canMutate)
                {
                    CreateInlineButton(header, "Add", () =>
                    {
                        AddListElement(elementType);
                    });
                }
                else
                {
                    AddInfoLabel(header, "List is read-only.");
                }

                var listNode = _container.NewChild();
                EnsureVerticalLayout(listNode);

                var sampleList = GetListSample();
                if (sampleList is null || sampleList.Count == 0)
                {
                    AddInfoLabel(listNode, "<empty>");
                    return;
                }

                for (int i = 0; i < sampleList.Count; i++)
                    BuildListRow(listNode, elementType, i, canMutate);
            }

            private IList? GetListSample()
            {
                foreach (var owner in _owners)
                {
                    if (_property.GetValue(owner) is IList list)
                        return list;
                }
                return null;
            }

            private void BuildListRow(SceneNode parent, Type elementType, int index, bool canMutate)
            {
                var row = parent.NewChild();
                var splitter = row.SetTransform<UIDualSplitTransform>();
                splitter.VerticalSplit = false;
                splitter.FirstFixedSize = true;
                splitter.FixedSize = RowHeaderWidth;

                var labelNode = row.NewChild();
                var labelLayout = labelNode.SetTransform<UIListTransform>();
                labelLayout.DisplayHorizontal = true;
                labelLayout.ItemSpacing = 4.0f;

                labelNode.NewChild<UITextComponent>(out var label);
                label.Text = $"[{index}]";
                label.FontSize = EditorUI.Styles.PropertyNameFontSize;
                label.HorizontalAlignment = EHorizontalAlignment.Left;
                label.VerticalAlignment = EVerticalAlignment.Center;
                label.Color = EditorUI.Styles.PropertyNameTextColor;
                label.BoundableTransform.Margins = new Vector4(4.0f, 2.0f, 4.0f, 2.0f);

                if (canMutate)
                {
                    CreateInlineButton(labelNode, "Remove", () => RemoveListElement(index));
                }

                var valueNode = row.NewChild();
                var bindingType = typeof(ListElementBinding<>).MakeGenericType(elementType);
                var bindingProperty = bindingType.GetProperty(nameof(ListElementBinding<object>.Value))!;
                var targets = CreateListBindings(bindingType, index);
                BuildValueEditor(valueNode, elementType, bindingProperty, targets);
            }

            private object[] CreateListBindings(Type bindingType, int index)
            {
                List<object> bindings = new();
                foreach (var owner in _owners)
                {
                    if (_property.GetValue(owner) is not IList list)
                        continue;
                    if (index < 0 || index >= list.Count)
                        continue;

                    var instance = Activator.CreateInstance(bindingType, _property, owner, index);
                    if (instance is not null)
                        bindings.Add(instance);
                }
                return bindings.ToArray();
            }

            private bool HasMutableList()
            {
                foreach (var owner in _owners)
                {
                    if (_property.GetValue(owner) is IList list && !list.IsReadOnly && !list.IsFixedSize)
                        return true;
                }
                return false;
            }

            private void AddListElement(Type elementType)
            {
                object? newValue = ImGuiEditorUtilities.CreateDefaultElement(elementType);
                foreach (var owner in _owners)
                {
                    if (_property.GetValue(owner) is not IList list || list.IsReadOnly || list.IsFixedSize)
                        continue;

                    try
                    {
                        list.Add(ConvertValue(newValue, elementType));
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex, $"Failed to add list element to '{_property.Name}'.");
                    }
                }

                Rebuild();
            }

            private void RemoveListElement(int index)
            {
                foreach (var owner in _owners)
                {
                    if (_property.GetValue(owner) is not IList list || list.IsReadOnly || list.IsFixedSize)
                        continue;

                    if (index < 0 || index >= list.Count)
                        continue;

                    try
                    {
                        list.RemoveAt(index);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex, $"Failed to remove list element from '{_property.Name}'.");
                    }
                }

                Rebuild();
            }

            private void RenderLinkedList(object sample, Type elementType)
            {
                int count = sample is ICollection collection ? collection.Count : 0;
                AddInfoLabel(_container, $"Count: {count}");

                bool canMutate = HasLinkedListTargets();
                var header = _container.NewChild();
                var headerLayout = header.SetTransform<UIListTransform>();
                headerLayout.DisplayHorizontal = true;
                headerLayout.ItemSpacing = 6.0f;

                if (canMutate)
                {
                    CreateInlineButton(header, "Add", () => AddLinkedListElement(elementType));
                }
                else
                {
                    AddInfoLabel(header, "LinkedList is read-only.");
                }

                var values = EnumerateLinkedListValues(sample).ToList();
                var listNode = _container.NewChild();
                EnsureVerticalLayout(listNode);

                if (values.Count == 0)
                {
                    AddInfoLabel(listNode, "<empty>");
                    return;
                }

                for (int i = 0; i < values.Count; i++)
                    BuildLinkedListRow(listNode, elementType, i, canMutate);
            }

            private IEnumerable<object?> EnumerateLinkedListValues(object linkedList)
            {
                if (linkedList is not IEnumerable enumerable)
                    yield break;

                foreach (var value in enumerable)
                    yield return value;
            }

            private bool HasLinkedListTargets()
            {
                foreach (var owner in _owners)
                {
                    var value = _property.GetValue(owner);
                    if (value is null)
                        continue;
                    if (IsLinkedListInstance(value, out _))
                        return true;
                }
                return false;
            }

            private void BuildLinkedListRow(SceneNode parent, Type elementType, int index, bool canMutate)
            {
                var row = parent.NewChild();
                var splitter = row.SetTransform<UIDualSplitTransform>();
                splitter.VerticalSplit = false;
                splitter.FirstFixedSize = true;
                splitter.FixedSize = RowHeaderWidth;

                var labelNode = row.NewChild();
                var labelLayout = labelNode.SetTransform<UIListTransform>();
                labelLayout.DisplayHorizontal = true;
                labelLayout.ItemSpacing = 4.0f;

                labelNode.NewChild<UITextComponent>(out var label);
                label.Text = $"Node {index}";
                label.FontSize = EditorUI.Styles.PropertyNameFontSize;
                label.HorizontalAlignment = EHorizontalAlignment.Left;
                label.VerticalAlignment = EVerticalAlignment.Center;
                label.Color = EditorUI.Styles.PropertyNameTextColor;
                label.BoundableTransform.Margins = new Vector4(4.0f, 2.0f, 4.0f, 2.0f);

                if (canMutate)
                {
                    CreateInlineButton(labelNode, "Remove", () => RemoveLinkedListElement(elementType, index));
                }

                var valueNode = row.NewChild();
                var bindingType = typeof(LinkedListElementBinding<>).MakeGenericType(elementType);
                var bindingProperty = bindingType.GetProperty(nameof(LinkedListElementBinding<object>.Value))!;
                var targets = CreateLinkedListBindings(bindingType, index);
                BuildValueEditor(valueNode, elementType, bindingProperty, targets);
            }

            private object[] CreateLinkedListBindings(Type bindingType, int index)
            {
                List<object> bindings = new();
                foreach (var owner in _owners)
                {
                    var value = _property.GetValue(owner);
                    if (value is null || !IsLinkedListInstance(value, out _))
                        continue;

                    var instance = Activator.CreateInstance(bindingType, _property, owner, index);
                    if (instance is not null)
                        bindings.Add(instance);
                }
                return bindings.ToArray();
            }

            private void AddLinkedListElement(Type elementType)
            {
                object? newValue = ImGuiEditorUtilities.CreateDefaultElement(elementType);
                foreach (var owner in _owners)
                {
                    var collection = _property.GetValue(owner);
                    if (collection is null)
                        continue;

                    var listType = typeof(LinkedList<>).MakeGenericType(elementType);
                    if (!listType.IsInstanceOfType(collection))
                        continue;

                    var addLast = listType.GetMethod("AddLast", new[] { elementType });
                    if (addLast is null)
                        continue;

                    try
                    {
                        addLast.Invoke(collection, new[] { ConvertValue(newValue, elementType) });
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex, $"Failed to add linked list node for '{_property.Name}'.");
                    }
                }

                Rebuild();
            }

            private void RemoveLinkedListElement(Type elementType, int index)
            {
                foreach (var owner in _owners)
                {
                    var collection = _property.GetValue(owner);
                    if (collection is null)
                        continue;

                    var listType = typeof(LinkedList<>).MakeGenericType(elementType);
                    if (!listType.IsInstanceOfType(collection))
                        continue;

                    var node = GetLinkedListNode(collection, index);
                    if (node is null)
                        continue;

                    var removeMethod = listType.GetMethod("Remove", new[] { node.GetType() })
                        ?? listType.GetMethod("Remove", new[] { elementType });

                    try
                    {
                        if (removeMethod?.GetParameters().Length == 1 && removeMethod.GetParameters()[0].ParameterType == node.GetType())
                            removeMethod.Invoke(collection, new[] { node });
                        else if (removeMethod is not null)
                            removeMethod.Invoke(collection, new[] { node.GetType().GetProperty("Value")?.GetValue(node) });
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex, $"Failed to remove linked list node for '{_property.Name}'.");
                    }
                }

                Rebuild();
            }

            private static object? GetLinkedListNode(object linkedList, int index)
            {
                var type = linkedList.GetType();
                var node = type.GetProperty("First")?.GetValue(linkedList);
                if (node is null)
                    return null;

                for (int i = 0; i < index && node is not null; i++)
                    node = node.GetType().GetProperty("Next")?.GetValue(node);

                return node;
            }

            private void RenderGenericCollection(ICollection sample, Type elementType)
            {
                List<object?> buffer = BuildBuffer(sample);
                AddInfoLabel(_container, $"Count: {buffer.Count}");

                bool canMutate = HasMutableCollection(elementType);
                var header = _container.NewChild();
                var headerLayout = header.SetTransform<UIListTransform>();
                headerLayout.DisplayHorizontal = true;
                headerLayout.ItemSpacing = 6.0f;

                if (canMutate)
                {
                    CreateInlineButton(header, "Add", () =>
                    {
                        buffer.Add(ImGuiEditorUtilities.CreateDefaultElement(elementType));
                        ApplyCollectionBuffer(buffer, elementType);
                        Rebuild();
                    });
                }
                else
                {
                    AddInfoLabel(header, "Collection is read-only.");
                }

                var listNode = _container.NewChild();
                EnsureVerticalLayout(listNode);

                if (buffer.Count == 0)
                {
                    AddInfoLabel(listNode, "<empty>");
                    return;
                }

                for (int i = 0; i < buffer.Count; i++)
                    BuildCollectionBufferRow(listNode, elementType, buffer, i, canMutate);
            }

            private void BuildCollectionBufferRow(SceneNode parent, Type elementType, List<object?> buffer, int index, bool canMutate)
            {
                var row = parent.NewChild();
                var splitter = row.SetTransform<UIDualSplitTransform>();
                splitter.VerticalSplit = false;
                splitter.FirstFixedSize = true;
                splitter.FixedSize = RowHeaderWidth;

                var labelNode = row.NewChild();
                var labelLayout = labelNode.SetTransform<UIListTransform>();
                labelLayout.DisplayHorizontal = true;
                labelLayout.ItemSpacing = 4.0f;

                labelNode.NewChild<UITextComponent>(out var label);
                label.Text = $"Element {index}";
                label.FontSize = EditorUI.Styles.PropertyNameFontSize;
                label.HorizontalAlignment = EHorizontalAlignment.Left;
                label.VerticalAlignment = EVerticalAlignment.Center;
                label.Color = EditorUI.Styles.PropertyNameTextColor;
                label.BoundableTransform.Margins = new Vector4(4.0f, 2.0f, 4.0f, 2.0f);

                if (canMutate)
                {
                    CreateInlineButton(labelNode, "Remove", () =>
                    {
                        buffer.RemoveAt(index);
                        ApplyCollectionBuffer(buffer, elementType);
                        Rebuild();
                    });
                }

                var valueNode = row.NewChild();
                var bindingType = typeof(CollectionBufferProxy<>).MakeGenericType(elementType);
                var bindingProperty = bindingType.GetProperty(nameof(CollectionBufferProxy<object>.Value))!;
                var proxy = Activator.CreateInstance(bindingType, buffer, index, (Action)(() => ApplyCollectionBuffer(buffer, elementType)));
                BuildValueEditor(valueNode, elementType, bindingProperty, proxy is null ? Array.Empty<object>() : new[] { proxy });
            }

            private bool HasMutableCollection(Type elementType)
            {
                foreach (var owner in _owners)
                {
                    var collection = _property.GetValue(owner);
                    if (collection is null)
                        continue;
                    var accessor = GetCollectionAccessor(collection.GetType());
                    if (accessor?.CanMutate == true)
                        return true;
                }
                return false;
            }

            private void ApplyCollectionBuffer(List<object?> buffer, Type elementType)
            {
                foreach (var owner in _owners)
                {
                    var collection = _property.GetValue(owner);
                    if (collection is null)
                        continue;

                    var accessor = GetCollectionAccessor(collection.GetType());
                    if (accessor is null)
                        continue;

                    try
                    {
                        accessor.Apply(collection, buffer, elementType);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex, $"Failed to update collection '{_property.Name}'.");
                    }
                }
            }
        }

        private sealed class CollectionAccessor
        {
            private readonly MethodInfo? _clear;
            private readonly MethodInfo? _add;
            private readonly Type? _addParameter;

            private CollectionAccessor(MethodInfo? clear, MethodInfo? add)
            {
                _clear = clear;
                _add = add;
                _addParameter = add?.GetParameters().FirstOrDefault()?.ParameterType;
            }

            public bool CanMutate => _clear is not null && _add is not null;

            public void Apply(object collection, IEnumerable<object?> buffer, Type fallbackType)
            {
                if (!CanMutate)
                    return;

                _clear!.Invoke(collection, null);
                foreach (var entry in buffer)
                    _add!.Invoke(collection, new[] { ConvertValue(entry, _addParameter ?? fallbackType) });
            }

            public static CollectionAccessor? Create(Type type)
            {
                MethodInfo? clear = FindMethod(type, "Clear", 0);
                MethodInfo? add = FindMethod(type, "Add", 1);

                if (clear is null || add is null)
                    return null;

                return new CollectionAccessor(clear, add);
            }
        }

        private static CollectionAccessor? GetCollectionAccessor(Type type)
        {
            if (!CollectionAccessorCache.TryGetValue(type, out var accessor))
            {
                accessor = CollectionAccessor.Create(type);
                CollectionAccessorCache[type] = accessor;
            }
            return accessor;
        }

        private static MethodInfo? FindMethod(Type type, string name, int parameterCount)
        {
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var method = type.GetMethods(flags).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == parameterCount);
            if (method is not null)
                return method;

            foreach (var iface in type.GetInterfaces())
            {
                var map = type.GetInterfaceMap(iface);
                for (int i = 0; i < map.InterfaceMethods.Length; i++)
                {
                    var ifaceMethod = map.InterfaceMethods[i];
                    if (ifaceMethod.Name != name)
                        continue;
                    if (ifaceMethod.GetParameters().Length != parameterCount)
                        continue;
                    return map.TargetMethods[i];
                }
            }

            return null;
        }

        private static void BuildValueEditor(SceneNode node, Type valueType, PropertyInfo bindingProperty, object[] targets)
        {
            if (bindingProperty is null || targets.Length == 0)
            {
                AddInfoLabel(node, "<No editable targets>");
                return;
            }

            var editor = CreateNew(valueType);
            if (editor is null)
            {
                AddInfoLabel(node, $"No editor for {valueType.Name}.");
                return;
            }

            editor(node, bindingProperty, targets.Cast<object?>().ToArray());
        }

        private static List<object> CollectOwners(object?[]? objects)
        {
            List<object> owners = new();
            if (objects is null)
                return owners;

            foreach (var obj in objects)
                if (obj is not null)
                    owners.Add(obj);
            return owners;
        }

        private static List<object?> BuildBuffer(IEnumerable source)
        {
            List<object?> buffer = new();
            foreach (var item in source)
                buffer.Add(item);
            return buffer;
        }

        private static bool ImplementsGenericInterface(Type type, Type genericDefinition)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == genericDefinition)
                return true;

            foreach (var iface in type.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == genericDefinition)
                    return true;
            }

            return false;
        }

        private static bool IsLinkedListType(Type type)
        {
            while (type is not null && type != typeof(object))
            {
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(LinkedList<>))
                    return true;
                type = type.BaseType!;
            }
            return false;
        }

        private static bool IsLinkedListInstance(object value, out Type elementType)
        {
            var type = value.GetType();
            while (type is not null && type != typeof(object))
            {
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(LinkedList<>))
                {
                    elementType = type.GetGenericArguments()[0];
                    return true;
                }
                type = type.BaseType!;
            }

            elementType = typeof(object);
            return false;
        }

        private static (Type KeyType, Type ValueType) ResolveDictionaryTypes(Type declaredType, Type runtimeType)
        {
            if (TryGetDictionaryArgs(declaredType, out var key, out var value))
                return (key, value);
            if (TryGetDictionaryArgs(runtimeType, out key, out value))
                return (key, value);
            return (typeof(object), typeof(object));
        }

        private static bool TryGetDictionaryArgs(Type type, out Type key, out Type value)
        {
            if (type.IsGenericType)
            {
                var def = type.GetGenericTypeDefinition();
                if (def == typeof(IDictionary<,>) || def == typeof(Dictionary<,>))
                {
                    var args = type.GetGenericArguments();
                    key = args[0];
                    value = args[1];
                    return true;
                }
            }

            foreach (var iface in type.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IDictionary<,>))
                {
                    var args = iface.GetGenericArguments();
                    key = args[0];
                    value = args[1];
                    return true;
                }
            }

            key = typeof(object);
            value = typeof(object);
            return false;
        }

        private static Type ResolveElementType(Type declaredType, Type runtimeType)
        {
            if (declaredType.IsArray)
                return declaredType.GetElementType() ?? typeof(object);

            if (declaredType.IsGenericType)
            {
                var args = declaredType.GetGenericArguments();
                if (args.Length == 1)
                    return args[0];
            }

            var enumerableIface = declaredType.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            if (enumerableIface is not null)
                return enumerableIface.GetGenericArguments()[0];

            if (runtimeType.IsGenericType)
            {
                var args = runtimeType.GetGenericArguments();
                if (args.Length == 1)
                    return args[0];
            }

            enumerableIface = runtimeType.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            if (enumerableIface is not null)
                return enumerableIface.GetGenericArguments()[0];

            return typeof(object);
        }

        private static void CreateInlineButton(SceneNode parent, string text, Action onClick)
        {
            var buttonNode = parent.NewChild<UIButtonComponent, UIMaterialComponent>(out var button, out var background);
            EditorUI.Styles.UpdateButton(button);
            background.Material = XRMaterial.CreateUnlitColorMaterialForward(ColorF4.Transparent);
            var tfm = buttonNode.SetTransform<UIBoundableTransform>();
            tfm.Width = ButtonWidth;
            tfm.Height = ButtonHeight;
            tfm.Margins = new Vector4(2.0f);

            buttonNode.NewChild<UITextComponent>(out var label);
            label.Text = text;
            label.FontSize = EditorUI.Styles.PropertyInputFontSize ?? 14.0f;
            label.Color = EditorUI.Styles.ButtonTextColor;
            label.HorizontalAlignment = EHorizontalAlignment.Center;
            label.VerticalAlignment = EVerticalAlignment.Center;

            button.RegisterClickActions(_ => onClick());
        }

        private static object? ConvertValue(object? value, Type targetType)
        {
            if (targetType is null)
                return value;

            targetType = Nullable.GetUnderlyingType(targetType) ?? targetType;

            if (value is null)
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;

            if (targetType.IsInstanceOfType(value))
                return value;

            try
            {
                if (targetType.IsEnum)
                {
                    if (value is string s && Enum.IsDefined(targetType, s))
                        return Enum.Parse(targetType, s, true);
                    return Enum.ToObject(targetType, value);
                }

                return Convert.ChangeType(value, targetType);
            }
            catch
            {
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
            }
        }

        private static bool IsNumericType(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            return type == typeof(byte)
                || type == typeof(sbyte)
                || type == typeof(short)
                || type == typeof(ushort)
                || type == typeof(int)
                || type == typeof(uint)
                || type == typeof(long)
                || type == typeof(ulong)
                || type == typeof(float)
                || type == typeof(double)
                || type == typeof(decimal);
        }

        private sealed class ListElementBinding<T>
        {
            private readonly PropertyInfo _property;
            private readonly object _owner;
            private readonly int _index;

            public ListElementBinding(PropertyInfo property, object owner, int index)
            {
                _property = property;
                _owner = owner;
                _index = index;
            }

            public T? Value
            {
                get
                {
                    if (_property.GetValue(_owner) is not IList list)
                        return default;
                    if (_index < 0 || _index >= list.Count)
                        return default;
                    return list[_index] is T value ? value : (T?)ConvertValue(list[_index], typeof(T));
                }
                set
                {
                    if (_property.GetValue(_owner) is not IList list || list.IsReadOnly || list.IsFixedSize)
                        return;
                    if (_index < 0 || _index >= list.Count)
                        return;
                    list[_index] = ConvertValue(value, typeof(T));
                }
            }
        }

        private sealed class DictionaryValueBinding<TKey, TValue>
        {
            private readonly PropertyInfo _property;
            private readonly object _owner;
            private readonly object? _key;

            public DictionaryValueBinding(PropertyInfo property, object owner, object? key)
            {
                _property = property;
                _owner = owner;
                _key = key;
            }

            public TValue? Value
            {
                get
                {
                    if (_property.GetValue(_owner) is not IDictionary dictionary)
                        return default;
                    if (_key is null)
                        return dictionary.Contains(null!) ? (TValue?)ConvertValue(dictionary[null!], typeof(TValue)) : default;
                    if (!dictionary.Contains(_key))
                        return default;
                    return dictionary[_key] is TValue value ? value : (TValue?)ConvertValue(dictionary[_key], typeof(TValue));
                }
                set
                {
                    if (_property.GetValue(_owner) is not IDictionary dictionary || dictionary.IsReadOnly)
                        return;
                    if (_key is null && dictionary.Contains(null!))
                        dictionary[null!] = ConvertValue(value, typeof(TValue));
                    else if (_key is not null && dictionary.Contains(_key))
                        dictionary[_key] = ConvertValue(value, typeof(TValue));
                }
            }
        }

        private sealed class LinkedListElementBinding<T>
        {
            private readonly PropertyInfo _property;
            private readonly object _owner;
            private readonly int _index;

            public LinkedListElementBinding(PropertyInfo property, object owner, int index)
            {
                _property = property;
                _owner = owner;
                _index = index;
            }

            public T? Value
            {
                get
                {
                    if (_property.GetValue(_owner) is not LinkedList<T> list)
                        return default;
                    var node = GetNode(list, _index);
                    return node is null ? default : node.Value;
                }
                set
                {
                    if (_property.GetValue(_owner) is not LinkedList<T> list)
                        return;
                    var node = GetNode(list, _index);
                    if (node is not null)
                        node.Value = value!;
                }
            }

            private static LinkedListNode<T>? GetNode(LinkedList<T> list, int index)
            {
                var node = list.First;
                for (int i = 0; i < index && node is not null; i++)
                    node = node.Next;
                return node;
            }
        }

        private sealed class CollectionBufferProxy<T>
        {
            private readonly List<object?> _buffer;
            private readonly int _index;
            private readonly Action _apply;

            public CollectionBufferProxy(List<object?> buffer, int index, Action apply)
            {
                _buffer = buffer;
                _index = index;
                _apply = apply;
            }

            public T? Value
            {
                get => _buffer[_index] is T value ? value : (T?)ConvertValue(_buffer[_index], typeof(T));
                set
                {
                    _buffer[_index] = value;
                    _apply();
                }
            }
        }
    }
}
