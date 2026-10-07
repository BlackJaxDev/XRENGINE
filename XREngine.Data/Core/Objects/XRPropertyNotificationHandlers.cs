namespace XREngine.Data.Core
{
    /// <summary>
    /// Creates property handlers that exclude unrelated notifications before XRBase creates event arguments.
    /// </summary>
    public static class XRPropertyNotificationHandlers
    {
        /// <summary>
        /// Creates a changed handler for the specified property names. Cache the result for subscription and removal.
        /// </summary>
        public static XRPropertyChangedEventHandler FilterChanged(
            XRPropertyChangedEventHandler handler, params string[] propertyNames)
        {
            ArgumentNullException.ThrowIfNull(handler);
            ArgumentNullException.ThrowIfNull(propertyNames);
            return new ChangedHandler(handler, (string[])propertyNames.Clone()).Invoke;
        }

        /// <summary>
        /// Creates a changed handler with a condition checked before event arguments are created.
        /// Cache the result. The condition must not modify state and must be safe on the caller's thread.
        /// </summary>
        public static XRPropertyChangedEventHandler FilterChangedWhen(
            XRPropertyChangedEventHandler handler, Func<object?, string?, bool> condition)
        {
            ArgumentNullException.ThrowIfNull(handler);
            ArgumentNullException.ThrowIfNull(condition);
            return new ConditionalChangedHandler(handler, condition).Invoke;
        }

        /// <summary>
        /// Creates a changing handler for the specified property names. Cache the result for subscription and removal.
        /// </summary>
        public static XRPropertyChangingEventHandler FilterChanging(
            XRPropertyChangingEventHandler handler, params string[] propertyNames)
        {
            ArgumentNullException.ThrowIfNull(handler);
            ArgumentNullException.ThrowIfNull(propertyNames);
            return new ChangingHandler(handler, (string[])propertyNames.Clone()).Invoke;
        }

        internal static bool Accepts(Delegate handler, object? sender, string? propertyName)
            => handler.Target is not PropertyFilter filter || filter.Accepts(sender, propertyName);

        private abstract class PropertyFilter(string[] propertyNames)
        {
            public virtual bool Accepts(object? sender, string? propertyName)
            {
                // Null and empty names indicate that all properties can have changed.
                if (string.IsNullOrEmpty(propertyName))
                    return true;
                foreach (string name in propertyNames)
                    if (string.Equals(name, propertyName, StringComparison.Ordinal))
                        return true;
                return false;
            }
        }

        private sealed class ChangedHandler(XRPropertyChangedEventHandler handler, string[] propertyNames)
            : PropertyFilter(propertyNames)
        {
            public void Invoke(object? sender, IXRPropertyChangedEventArgs args)
            {
                if (Accepts(sender, args.PropertyName))
                    handler(sender, args);
            }
        }

        private sealed class ChangingHandler(XRPropertyChangingEventHandler handler, string[] propertyNames)
            : PropertyFilter(propertyNames)
        {
            public void Invoke(object? sender, IXRPropertyChangingEventArgs args)
            {
                if (Accepts(sender, args.PropertyName))
                    handler(sender, args);
            }
        }

        private sealed class ConditionalChangedHandler(
            XRPropertyChangedEventHandler handler, Func<object?, string?, bool> condition)
            : PropertyFilter([])
        {
            public override bool Accepts(object? sender, string? propertyName)
                => condition(sender, propertyName);

            public void Invoke(object? sender, IXRPropertyChangedEventArgs args)
            {
                if (Accepts(sender, args.PropertyName))
                    handler(sender, args);
            }
        }
    }
}
