namespace XREngine.Rendering.UI;

/// <summary>Names one immutable record leaf in a paged range owner.</summary>
internal readonly record struct UIRecordLeafClaim<T>(UIRecordStoreNode<T> Leaf) where T : struct;
