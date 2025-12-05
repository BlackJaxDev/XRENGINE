namespace XREngine.Rendering
{
    /// <summary>
    /// A material instance that inherits from another material.
    /// Textures and properties can be overridden on the instance.
    /// </summary>
    public class XRMaterialInstance : XRMaterialBase
    {
        private XRMaterialBase? _inheritedMaterial;
        public XRMaterialBase? InheritedMaterial
        {
            get => _inheritedMaterial;
            set
            {
                //_inheritedMaterial.Loaded -= MaterialLoaded;
                _inheritedMaterial = value;
                //_inheritedMaterial.Loaded += MaterialLoaded;
            }
        }
        //private void MaterialLoaded(XRMaterial mat)
        //{

        //}
    }
}
