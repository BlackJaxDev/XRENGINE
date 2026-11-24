using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.ObjectGraphVisitors;

namespace XREngine;

/// <summary>
/// Finds the objects that a YAML document references more than once and gives each one an anchor
/// name that is unique in the whole document. The emitter writes the anchor on the first occurrence
/// and an alias on each later occurrence.
/// </summary>
/// <remarks>
/// YamlDotNet creates a new anchor assigner for each nested serialize call. A type converter makes
/// such a call when it writes a child value with its <see cref="ObjectSerializer"/>, for example the
/// transform converter for each child transform and the asset converter for each inline asset. The
/// default assigner starts its names at <c>o0</c> in each call, so one document can define the same
/// anchor name more than once. A reader resolves an alias to the most recent anchor with that name,
/// so an alias that the outer block wrote for a component can resolve to an object from an earlier
/// nested block, such as a <c>SubMeshLOD</c>. This assigner takes its numbers from a per-thread
/// counter that continues through the nested calls of one document. <see cref="DocumentScopedYamlSerializer"/>
/// restarts the counter for each top-level document, so a saved file gets the same names each time.
/// </remarks>
public sealed class DocumentUniqueAnchorAssigner : PreProcessingPhaseObjectGraphVisitorSkeleton, IAliasProvider
{
    /// <summary>
    /// The next anchor number on this thread. <see cref="BeginDocument"/> restarts it at zero for a
    /// top-level document, and nested serialize calls of that document continue from the current value.
    /// </summary>
    [ThreadStatic]
    private static uint t_nextAnchorId;

    /// <summary>
    /// The number of open documents on this thread. A converter can serialize a separate document
    /// while the outer document is open; that inner document keeps the counter, so it does not
    /// restart the names of the outer document.
    /// </summary>
    [ThreadStatic]
    private static int t_openDocuments;

    /// <summary>
    /// Maps each visited object to its anchor name, or to <see cref="AnchorName.Empty"/> while the
    /// object has been seen only once. Keys use reference identity, so two equal value objects never share an anchor.
    /// </summary>
    private readonly Dictionary<object, AnchorName> _anchors = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Creates the assigner for one serialize call. YamlDotNet calls this for the top-level call and
    /// for each nested call of a document; all of them take names from the same counter.
    /// </summary>
    public DocumentUniqueAnchorAssigner(IEnumerable<IYamlTypeConverter> typeConverters)
        : base(typeConverters)
    {
    }

    /// <summary>
    /// Opens a document on this thread. The first open document restarts the anchor counter at zero.
    /// Call <see cref="EndDocument"/> when the document is written, also when the write fails.
    /// </summary>
    public static void BeginDocument()
    {
        if (t_openDocuments++ == 0)
            t_nextAnchorId = 0;
    }

    /// <summary>Closes the document that <see cref="BeginDocument"/> opened.</summary>
    public static void EndDocument()
    {
        if (t_openDocuments > 0)
            t_openDocuments--;
    }

    /// <summary>
    /// Replaces the default anchor assigner of <paramref name="builder"/> with this assigner, and
    /// connects the emission visitor that writes anchors and aliases to it.
    /// </summary>
    public static SerializerBuilder Register(SerializerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .WithPreProcessingPhaseObjectGraphVisitor(
                typeConverters => new DocumentUniqueAnchorAssigner(typeConverters),
                w => w.InsteadOf<AnchorAssigner>())
            .WithEmissionPhaseObjectGraphVisitor(
                args => new AnchorAssigningObjectGraphVisitor(
                    args.InnerVisitor,
                    args.EventEmitter,
                    args.GetPreProcessingPhaseObjectGraphVisitor<DocumentUniqueAnchorAssigner>()),
                w => w.InsteadOf<AnchorAssigningObjectGraphVisitor>());
    }

    /// <summary>
    /// Stops the traversal at an object that was already visited. On the second visit the object
    /// gets its anchor name, and the emission pass writes an alias for each later occurrence.
    /// </summary>
    protected override bool Enter(IObjectDescriptor value, ObjectSerializer serializer)
    {
        if (value.Value is null || !_anchors.TryGetValue(value.Value, out AnchorName anchor))
            return true;

        if (anchor.IsEmpty)
            _anchors[value.Value] = new AnchorName("o" + (t_nextAnchorId++).ToString(CultureInfo.InvariantCulture));
        return false;
    }

    protected override bool EnterMapping(IPropertyDescriptor key, IObjectDescriptor value, ObjectSerializer serializer)
        => true;

    protected override bool EnterMapping(IObjectDescriptor key, IObjectDescriptor value, ObjectSerializer serializer)
        => true;

    /// <summary>Scalars never get anchors; each occurrence writes its value.</summary>
    protected override void VisitScalar(IObjectDescriptor scalar, ObjectSerializer serializer)
    {
    }

    protected override void VisitMappingStart(IObjectDescriptor mapping, Type keyType, Type valueType, ObjectSerializer serializer)
        => RecordFirstVisit(mapping);

    protected override void VisitMappingEnd(IObjectDescriptor mapping, ObjectSerializer serializer)
    {
    }

    protected override void VisitSequenceStart(IObjectDescriptor sequence, Type elementType, ObjectSerializer serializer)
        => RecordFirstVisit(sequence);

    protected override void VisitSequenceEnd(IObjectDescriptor sequence, ObjectSerializer serializer)
    {
    }

    /// <summary>
    /// Returns the anchor name of <paramref name="target"/>, or <see cref="AnchorName.Empty"/> when the
    /// document references it only once.
    /// </summary>
    public AnchorName GetAlias(object target)
        => target is not null && _anchors.TryGetValue(target, out AnchorName anchor) ? anchor : AnchorName.Empty;

    /// <summary>Records the first visit of a mapping or sequence object, with no anchor yet.</summary>
    private void RecordFirstVisit(IObjectDescriptor value)
    {
        if (value.Value is not null)
            _anchors.TryAdd(value.Value, AnchorName.Empty);
    }
}
