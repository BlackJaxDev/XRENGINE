using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;

namespace XREngine;

/// <summary>
/// Tracks the current YAML object-graph depth for converters that decide whether
/// an asset should be emitted inline or as an external reference.
/// </summary>
public sealed class DepthTrackingEventEmitter(IEventEmitter nextEmitter) : ChainedEventEmitter(nextEmitter)
{
    [ThreadStatic]
    private static int _depth;

    public override void Emit(AliasEventInfo eventInfo, IEmitter emitter)
        => base.Emit(eventInfo, emitter);

    public override void Emit(ScalarEventInfo eventInfo, IEmitter emitter)
        => base.Emit(eventInfo, emitter);

    public override void Emit(MappingStartEventInfo eventInfo, IEmitter emitter)
    {
        _depth++;
        base.Emit(eventInfo, emitter);
    }

    public override void Emit(MappingEndEventInfo eventInfo, IEmitter emitter)
    {
        base.Emit(eventInfo, emitter);
        _depth--;
    }

    public override void Emit(SequenceStartEventInfo eventInfo, IEmitter emitter)
    {
        _depth++;
        base.Emit(eventInfo, emitter);
    }

    public override void Emit(SequenceEndEventInfo eventInfo, IEmitter emitter)
    {
        base.Emit(eventInfo, emitter);
        _depth--;
    }

    public static int CurrentDepth => _depth;
}
