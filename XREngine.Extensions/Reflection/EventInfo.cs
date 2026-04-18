using System.Reflection;

namespace XREngine.Extensions
{
    public static class EventInfoExtensions
    {
        private const BindingFlags AllInstanceFields = 
            BindingFlags.NonPublic |
            BindingFlags.Public |
            BindingFlags.Instance |
            BindingFlags.GetField;

        /// <summary>
        /// Retrieves all method delegates that have been added as subscribers to this event for the given object.
        /// </summary>
        /// <param name="eventInfo">The event to retrieve subscribers from.</param>
        /// <param name="obj">The object that owns the event.</param>
        /// <returns>All subscribing delegates.</returns>
        public static Delegate[] GetSubscribedMethods(this EventInfo eventInfo, object obj)
            => (eventInfo.DeclaringType?.GetField(eventInfo.Name, AllInstanceFields)?.GetValue(obj) as Delegate)?.GetInvocationList() ?? Array.Empty<Delegate>();
    }
}
