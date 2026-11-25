using XREngine.Core.Files;

namespace XREngine.UnitTests.Scene;

internal delegate XRAsset? SceneRootEnvelopeReader(ReadOnlySpan<byte> data, Type? expectedType);
