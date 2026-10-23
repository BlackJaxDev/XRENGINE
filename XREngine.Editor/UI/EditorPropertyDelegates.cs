using System.Linq.Expressions;
using System.Reflection;

namespace XREngine.Editor.UI;

/// <summary>Stores open delegates for one scalar editor property.</summary>
internal sealed class EditorPropertyDelegates
{
    public required Func<object, object?> Get { get; init; }
    public Action<object, object?>? Set { get; init; }

    public static EditorPropertyDelegates Create(PropertyInfo property)
    {
        if (property.GetMethod is null || property.GetMethod.IsStatic || property.GetIndexParameters().Length != 0)
            throw new ArgumentException("A readable instance scalar property is required.", nameof(property));
        var target = Expression.Parameter(typeof(object), "target");
        var access = Expression.Property(Expression.Convert(target, property.DeclaringType!), property);
        var getter = Expression.Lambda<Func<object, object?>>(
            Expression.Convert(access, typeof(object)), target).Compile();
        Action<object, object?>? setter = null;
        if (property.SetMethod is not null)
        {
            var value = Expression.Parameter(typeof(object), "value");
            setter = Expression.Lambda<Action<object, object?>>(
                Expression.Assign(access, Expression.Convert(value, property.PropertyType)), target, value).Compile();
        }
        return new EditorPropertyDelegates { Get = getter, Set = setter };
    }
}
