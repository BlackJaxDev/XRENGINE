using MemoryPack;
using System.Diagnostics;
using XREngine.Core.Files;
using XREngine.Data;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Unity
{
    [MemoryPackable(GenerateType.NoGenerate)]
    [XR3rdPartyExtensions(typeof(XREngine.Data.XRDefault3rdPartyImportOptions), "anim:static")]
    public partial class UnityAnimationClip : XRAsset
    {
        public class Wrapper
        {
            [YamlMember(Alias = "AnimationClip")]
            public UnityAnimationClip? Clip { get; set; }
        }
        public static UnityAnimationClip? Load3rdPartyStatic(string filePath)
        {
            // Load the Unity animation clip from the specified file path
            IDeserializer deserializer = new StaticDeserializerBuilder(new UnityStaticContext())
                .WithTagMapping(new TagName("tag:unity3d.com,2011:74"), typeof(Wrapper))
                .Build();
            return deserializer.Deserialize<Wrapper>(File.ReadAllText(filePath)).Clip;
        }

        [YamlMember(Alias = "m_ObjectHideFlags")]
        public int ObjectHideFlags { get; set; }

        [YamlMember(Alias = "m_PrefabParentObject")]
        public PrefabObject? PrefabParentObject { get; set; }

        [YamlMember(Alias = "m_PrefabInternal")]
        public PrefabObject? PrefabInternal { get; set; }

        [YamlMember(Alias = "m_Name")]
        public string? ClipName
        {
            get => Name;
            set => Name = value;
        }

        [YamlMember(Alias = "serializedVersion")]
        public int SerializedVersion { get; set; }

        [YamlMember(Alias = "m_Legacy")]
        public int Legacy { get; set; }

        [YamlMember(Alias = "m_Compressed")]
        public int Compressed { get; set; }

        [YamlMember(Alias = "m_UseHighQualityCurve")]
        public int UseHighQualityCurve { get; set; }

        [YamlMember(Alias = "m_RotationCurves")]
        public List<Curve>? RotationCurves { get; set; }

        [YamlMember(Alias = "m_CompressedRotationCurves")]
        public List<Curve>? CompressedRotationCurves { get; set; }

        [YamlMember(Alias = "m_EulerCurves")]
        public List<Curve>? EulerCurves { get; set; }

        [YamlMember(Alias = "m_PositionCurves")]
        public List<Curve>? PositionCurves { get; set; }

        [YamlMember(Alias = "m_ScaleCurves")]
        public List<Curve>? ScaleCurves { get; set; }

        [YamlMember(Alias = "m_FloatCurves")]
        public List<FloatCurve>? FloatCurves { get; set; }

        [YamlMember(Alias = "m_PPtrCurves")]
        public List<Curve>? PPtrCurves { get; set; }

        [YamlMember(Alias = "m_SampleRate")]
        public int SampleRate { get; set; }

        [YamlMember(Alias = "m_WrapMode")]
        public int WrapMode { get; set; }

        [YamlMember(Alias = "m_Bounds")]
        public UnityBounds? Bounds { get; set; }

        [YamlMember(Alias = "m_ClipBindingConstant")]
        public ClipBindingConstant? ClipBindingConstant { get; set; }

        [YamlMember(Alias = "m_AnimationClipSettings")]
        public AnimationClipSettings? AnimationClipSettings { get; set; }

        [YamlMember(Alias = "m_EditorCurves")]
        public List<FloatCurve>? EditorCurves { get; set; }

        [YamlMember(Alias = "m_EulerEditorCurves")]
        public List<Curve>? EulerEditorCurves { get; set; }

        [YamlMember(Alias = "m_HasGenericRootTransform")]
        public int HasGenericRootTransform { get; set; }

        [YamlMember(Alias = "m_HasMotionFloatCurves")]
        public int HasMotionFloatCurves { get; set; }

        [YamlMember(Alias = "m_GenerateMotionCurves")]
        public int GenerateMotionCurves { get; set; }

        [YamlMember(Alias = "m_Events")]
        public List<Event>? Events { get; set; }
    }

    public class PrefabObject
    {
        [YamlMember(Alias = "fileID")]
        public int FileID { get; set; }
    }

    public class Curve
    {
        // Define properties for Curve class
    }

    public class FloatCurve
    {
        [YamlMember(Alias = "curve")]
        public CurveData? Curve { get; set; }

        [YamlMember(Alias = "attribute")]
        public string? Attribute { get; set; }

        [YamlMember(Alias = "path")]
        public string? Path { get; set; }

        [YamlMember(Alias = "classID")]
        public int ClassID { get; set; }

        [YamlMember(Alias = "script")]
        public PrefabObject? Script { get; set; }
    }

    public class CurveData
    {
        [YamlMember(Alias = "serializedVersion")]
        public int SerializedVersion { get; set; }

        [YamlMember(Alias = "m_Curve")]
        public List<CurveKey>? Curve { get; set; }

        [YamlMember(Alias = "m_PreInfinity")]
        public int PreInfinity { get; set; }

        [YamlMember(Alias = "m_PostInfinity")]
        public int PostInfinity { get; set; }

        [YamlMember(Alias = "m_RotationOrder")]
        public int RotationOrder { get; set; }
    }

    public class CurveKey
    {
        [YamlMember(Alias = "serializedVersion")]
        public int SerializedVersion { get; set; }

        [YamlMember(Alias = "time")]
        public float Time { get; set; }

        [YamlMember(Alias = "value")]
        public float Value { get; set; }

        [YamlMember(Alias = "inSlope")]
        public float InSlope { get; set; }

        [YamlMember(Alias = "outSlope")]
        public float OutSlope { get; set; }

        [YamlMember(Alias = "tangentMode")]
        public int CombinedTangentMode { get; set; }
    }

    public enum TangentMode
    {
        //
        // Summary:
        //     The tangent can be freely set by dragging the tangent handle.
        Free,
        //
        // Summary:
        //     The tangents are automatically set to make the curve go smoothly through the
        //     key.
        Auto,
        //
        // Summary:
        //     The tangent points towards the neighboring key.
        Linear,
        //
        // Summary:
        //     The curve retains a constant value between two keys.
        Constant,
        //
        // Summary:
        //     The tangents are automatically set to make the curve go smoothly through the
        //     key.
        ClampedAuto
    }

    public class UnityBounds
    {
        [YamlMember(Alias = "m_Center")]
        public UnityVector3? Center { get; set; }

        [YamlMember(Alias = "m_Extent")]
        public UnityVector3? Extent { get; set; }
    }

    public class UnityVector3
    {
        [YamlMember(Alias = "x")]
        public float X { get; set; }

        [YamlMember(Alias = "y")]
        public float Y { get; set; }

        [YamlMember(Alias = "z")]
        public float Z { get; set; }
    }

    public class ClipBindingConstant
    {
        [YamlMember(Alias = "genericBindings")]
        public List<GenericBinding>? GenericBindings { get; set; }

        [YamlMember(Alias = "pptrCurveMapping")]
        public List<object>? PptrCurveMapping { get; set; }
    }

    public class GenericBinding
    {
        [YamlMember(Alias = "serializedVersion")]
        public int SerializedVersion { get; set; }

        [YamlMember(Alias = "path")]
        public string? Path { get; set; }

        [YamlMember(Alias = "attribute")]
        public string? Attribute { get; set; }

        [YamlMember(Alias = "script")]
        public PrefabObject? Script { get; set; }

        [YamlMember(Alias = "typeID")]
        public int TypeID { get; set; }

        [YamlMember(Alias = "customType")]
        public int CustomType { get; set; }

        [YamlMember(Alias = "isPPtrCurve")]
        public int IsPPtrCurve { get; set; }
    }

    public class AnimationClipSettings
    {
        [YamlMember(Alias = "serializedVersion")]
        public int SerializedVersion { get; set; }

        [YamlMember(Alias = "m_AdditiveReferencePoseClip")]
        public AdditiveReferencePoseClip? AdditiveReferencePoseClip { get; set; }

        [YamlMember(Alias = "m_AdditiveReferencePoseTime")]
        public float AdditiveReferencePoseTime { get; set; }

        [YamlMember(Alias = "m_StartTime")]
        public float StartTime { get; set; }

        [YamlMember(Alias = "m_StopTime")]
        public float StopTime { get; set; }

        [YamlMember(Alias = "m_OrientationOffsetY")]
        public float OrientationOffsetY { get; set; }

        [YamlMember(Alias = "m_Level")]
        public float Level { get; set; }

        [YamlMember(Alias = "m_CycleOffset")]
        public float CycleOffset { get; set; }

        [YamlMember(Alias = "m_HasAdditiveReferencePose")]
        public int HasAdditiveReferencePose { get; set; }

        [YamlMember(Alias = "m_LoopTime")]
        public int LoopTime { get; set; }

        [YamlMember(Alias = "m_LoopBlend")]
        public int LoopBlend { get; set; }

        [YamlMember(Alias = "m_LoopBlendOrientation")]
        public int LoopBlendOrientation { get; set; }

        [YamlMember(Alias = "m_LoopBlendPositionY")]
        public int LoopBlendPositionY { get; set; }

        [YamlMember(Alias = "m_LoopBlendPositionXZ")]
        public int LoopBlendPositionXZ { get; set; }

        [YamlMember(Alias = "m_KeepOriginalOrientation")]
        public int KeepOriginalOrientation { get; set; }

        [YamlMember(Alias = "m_KeepOriginalPositionY")]
        public int KeepOriginalPositionY { get; set; }

        [YamlMember(Alias = "m_KeepOriginalPositionXZ")]
        public int KeepOriginalPositionXZ { get; set; }

        [YamlMember(Alias = "m_HeightFromFeet")]
        public int HeightFromFeet { get; set; }

        [YamlMember(Alias = "m_Mirror")]
        public int Mirror { get; set; }
    }

    public class AdditiveReferencePoseClip
    {
        [YamlMember(Alias = "fileID")]
        public int FileID { get; set; }

        [YamlMember(Alias = "guid")]
        public string? Guid { get; set; }

        [YamlMember(Alias = "type")]
        public int Type { get; set; }
    }

    public class Event
    {
        // Define properties for Event class
    }
}
