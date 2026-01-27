namespace XREngine.Core.Reflection.Attributes
{
    public class TEnumDef : Attribute
    {
        public string DisplayName { get; set; }
        public string? Description { get; set; }
        public TEnumDef(string displayName, string? description = null)
        {
            DisplayName = displayName;
            Description = description;
        }
    }
}
