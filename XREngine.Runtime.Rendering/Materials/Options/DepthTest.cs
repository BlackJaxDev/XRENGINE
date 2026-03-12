using System.ComponentModel;
using XREngine.Data.Core;

namespace XREngine.Rendering.Models.Materials
{
    [Serializable]
    public class DepthTest : XRBase
    {
        [Browsable(false)]
        public bool IsEnabled => Enabled == ERenderParamUsage.Enabled;
        [Browsable(false)]
        public bool IsDisabled => Enabled == ERenderParamUsage.Disabled;
        [Browsable(false)]
        public bool IsUnchanged => Enabled == ERenderParamUsage.Unchanged;

        /// <summary>
        /// Determines if this material will test against the previously written depth value to determine if color fragments should be written or not.
        /// </summary>
        [Description("Determines if this material will test against the previously written depth value to determine if color fragments should be written or not.")]
        public ERenderParamUsage Enabled { get; set; } = ERenderParamUsage.Enabled;

        /// <summary>
        /// Determines if the material will update the depth value upon writing a new color fragment.
        /// </summary>
        [Description("Determines if the material will update the depth value upon writing a new color fragment.")]
        public bool UpdateDepth { get; set; } = true;

        /// <summary>
        /// Determines the pass condition to write a new color fragment. Usually less or lequal, meaning closer to the camera than the previous depth means a success.
        /// </summary>
        [Description("Determines the pass condition to write a new color fragment. Usually less or lequal, meaning closer to the camera than the previous depth means a success.")]
        public EComparison Function { get; set; } = EComparison.Lequal;

        public override string ToString()
            => Enabled == ERenderParamUsage.Unchanged 
                ? "Unchanged" 
                : Enabled == ERenderParamUsage.Disabled 
                    ? "Disabled" 
                    : $"[{Function}, Write Depth:{UpdateDepth}]";
    }
}
