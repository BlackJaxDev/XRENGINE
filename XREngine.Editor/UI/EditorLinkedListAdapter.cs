using System.Collections.Generic;
using System.Reflection;

namespace XREngine.Editor.UI;

/// <summary>Calls only methods on an exact BCL linked-list type.</summary>
internal sealed class EditorLinkedListAdapter
{
    private readonly Type _nodeType;
    private readonly PropertyInfo _first;
    private readonly PropertyInfo _next;
    private readonly PropertyInfo _value;
    private readonly PropertyInfo _nodeList;
    private readonly PropertyInfo _count;
    private readonly MethodInfo _addLast;
    private readonly MethodInfo _addFirst;
    private readonly MethodInfo _addAfter;
    private readonly MethodInfo _remove;

    private EditorLinkedListAdapter(Type type, Type elementType)
    {
        _nodeType = typeof(LinkedListNode<>).MakeGenericType(elementType);
        _first = type.GetProperty("First")!;
        _count = type.GetProperty("Count")!;
        _next = _nodeType.GetProperty("Next")!;
        _value = _nodeType.GetProperty("Value")!;
        _nodeList = _nodeType.GetProperty("List")!;
        _addLast = type.GetMethod("AddLast", [_nodeType])!;
        _addFirst = type.GetMethod("AddFirst", [_nodeType])!;
        _addAfter = type.GetMethod("AddAfter", [_nodeType, _nodeType])!;
        _remove = type.GetMethod("Remove", [_nodeType])!;
        ElementType = elementType;
    }

    public Type ElementType { get; }

    public static EditorLinkedListAdapter? For(object? list)
    {
        if (list is null) return null;
        Type type = list.GetType();
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(LinkedList<>))
            return null;
        Type elementType = type.GetGenericArguments()[0];
        return EditorListMutation.IsComparableElementType(elementType)
            ? new(type, elementType) : null;
    }

    public int Count(object list) => (int)_count.GetValue(list)!;
    public object? First(object list) => _first.GetValue(list);
    public object? Next(object node) => _next.GetValue(node);
    public object? Value(object node) => _value.GetValue(node);
    public object? OwningList(object node) => _nodeList.GetValue(node);
    public void Set(object node, object? value) => _value.SetValue(node, value);
    public object CreateNode(object? value) => Activator.CreateInstance(_nodeType, [value])!;
    public void Remove(object list, object node) => _remove.Invoke(list, [node]);
    public void Append(object list, object node) => _addLast.Invoke(list, [node]);
    public void Insert(object list, object? previous, object node)
    {
        if (previous is null) _addFirst.Invoke(list, [node]);
        else _addAfter.Invoke(list, [previous, node]);
    }

    public EditorLinkedListSnapshot Snapshot(object list)
    {
        int count = Count(list);
        object[] nodes = new object[count];
        object?[] values = new object?[count];
        object? node = First(list);
        for (int index = 0; index < count; index++)
        {
            if (node is null) throw new InvalidOperationException("The linked list changed during inspection.");
            nodes[index] = node;
            values[index] = Value(node);
            node = Next(node);
        }
        if (node is not null) throw new InvalidOperationException("The linked list changed during inspection.");
        return new(nodes, values);
    }

    public bool Matches(object list, EditorLinkedListSnapshot expected)
    {
        if (Count(list) != expected.Nodes.Length) return false;
        object? node = First(list);
        for (int index = 0; index < expected.Nodes.Length; index++)
        {
            if (!ReferenceEquals(node, expected.Nodes[index]) ||
                !EditorListMutation.SameValue(Value(node!), expected.Values[index]))
                return false;
            node = Next(node!);
        }
        return node is null;
    }
}
