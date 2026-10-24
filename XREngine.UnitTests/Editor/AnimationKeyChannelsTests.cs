using System.Numerics;
using NUnit.Framework;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class AnimationKeyChannelsTests
{
    [Test]
    public void ReplacingVectorChannelPreservesAllOtherChannels()
    {
        Vector4 original = new(1, 2, 3, 4);
        Assert.That(AnimationKeyChannels.TryReplace(original, 2, 9, out object? changed), Is.True);
        Assert.That(changed, Is.EqualTo(new Vector4(1, 2, 9, 4)));
        Assert.That(original, Is.EqualTo(new Vector4(1, 2, 3, 4)));
    }

    [Test]
    public void QuaternionChannelKeepsExactOtherComponentsAndRejectsInvalidValue()
    {
        Quaternion original = new(1, 2, 3, 4);
        Assert.That(AnimationKeyChannels.TryReplace(original, 3, -8, out object? changed), Is.True);
        Assert.That(changed, Is.EqualTo(new Quaternion(1, 2, 3, -8)));
        Assert.That(AnimationKeyChannels.TryReplace(original, 1, float.NaN, out _), Is.False);
        Assert.That(AnimationKeyChannels.TryReplace(original, 4, 8, out _), Is.False);
    }
}
