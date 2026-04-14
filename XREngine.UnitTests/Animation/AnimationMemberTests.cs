using System;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Animation;

namespace XREngine.UnitTests.Animation;

[TestFixture]
public sealed class AnimationMemberTests
{
    private static readonly FieldInfo PropertyCacheField =
        typeof(AnimationMember).GetField("_propertyCache", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Failed to locate AnimationMember._propertyCache.");

    [Test]
    public void ApplyAnimationValue_UsesTypedPropertyApplier_WhenReflectionCacheIsCleared()
    {
        var target = new FloatPropertyTarget();
        var member = new AnimationMember(nameof(FloatPropertyTarget.Value), EAnimationMemberType.Property);

        member.InitializeProperty(target);
        PropertyCacheField.SetValue(member, null);

        member.ApplyAnimationValue(7.25f);

        target.Value.ShouldBe(7.25f);
        target.SetterCallCount.ShouldBe(1);
    }

    private sealed class FloatPropertyTarget
    {
        private float _value;

        public int SetterCallCount { get; private set; }

        public float Value
        {
            get => _value;
            private set
            {
                SetterCallCount++;
                _value = value;
            }
        }
    }
}