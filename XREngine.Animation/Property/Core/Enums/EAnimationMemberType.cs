namespace XREngine.Animation
{
    public enum EAnimationMemberType
    {
        /// <summary>
        /// The member is a property of the current object.
        /// </summary>
        Property,
        /// <summary>
        /// The member is a field of the current object.
        /// </summary>
        Field,
        /// <summary>
        /// The member is a method of the current object.
        /// </summary>
        Method,
        /// <summary>
        /// Not a singular member to read from the current object, but a collection of members.
        /// </summary>
        Group,
    }
}
