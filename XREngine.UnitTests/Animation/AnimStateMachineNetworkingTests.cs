using NUnit.Framework;
using Shouldly;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using XREngine;
using XREngine.Animation;
using XREngine.Components;

namespace XREngine.UnitTests.Animation
{
    [TestFixture]
    public sealed class AnimStateMachineNetworkingTests
    {
        [Test]
        public void SchemaPacket_CreatesVariablesWithTypesAndDefaults()
        {
            var component = new AnimStateMachineComponent();

            var entries = new List<AnimStateMachine.AnimParameterSchemaEntry>
            {
                new("Flag", AnimStateMachine.AnimParameterType.Bool, BoolDefault: true, IntDefault: 0, FloatDefault: 0f),
                new("Count", AnimStateMachine.AnimParameterType.Int, BoolDefault: false, IntDefault: 123, FloatDefault: 0f),
                new("Blend", AnimStateMachine.AnimParameterType.Float, BoolDefault: false, IntDefault: 0, FloatDefault: 0.25f),
            };

            byte[] payload = CreateSchemaPayload(schemaVersion: 42, entries);
            component.ReceiveData("SCHEMA", payload);

            var sm = component.StateMachine;
            sm.Variables.ShouldContainKey("Flag");
            sm.Variables["Flag"].ShouldBeOfType<AnimBool>().Value.ShouldBeTrue();

            sm.Variables.ShouldContainKey("Count");
            sm.Variables["Count"].ShouldBeOfType<AnimInt>().Value.ShouldBe(123);

            sm.Variables.ShouldContainKey("Blend");
            sm.Variables["Blend"].ShouldBeOfType<AnimFloat>().Value.ShouldBe(0.25f);

            sm.ParameterSchemaVersion.ShouldBe(42);
        }

        [Test]
        public void ChangeIndexPacket_UpdatesCorrectVariable_ByIndex()
        {
            var component = new AnimStateMachineComponent();

            // 3 parameters => indexBits=2. We'll send one update, then an invalid index (3) to force receiver to stop.
            var schema = new List<AnimStateMachine.AnimParameterSchemaEntry>
            {
                new("A", AnimStateMachine.AnimParameterType.Bool, BoolDefault: false, IntDefault: 0, FloatDefault: 0f),
                new("B", AnimStateMachine.AnimParameterType.Bool, BoolDefault: false, IntDefault: 0, FloatDefault: 0f),
                new("C", AnimStateMachine.AnimParameterType.Bool, BoolDefault: false, IntDefault: 0, FloatDefault: 0f),
            };
            component.ReceiveData("SCHEMA", CreateSchemaPayload(schemaVersion: 1, schema));

            component.StateMachine.ParameterNameIdBitCount.ShouldBe(2);

            // Ordered by ordinal => A=0, B=1, C=2.
            int indexBits = component.StateMachine.ParameterNameIdBitCount;
            int bitOffset = 0;
            int totalBits = indexBits + 1 + indexBits;
            var bytes = new byte[(totalBits + 7) / 8];

            // Update B to true.
            WriteBits(bytes, ref bitOffset, value: 1u, bitCount: indexBits);
            WriteBits(bytes, ref bitOffset, value: 1u, bitCount: 1);

            // Terminator: invalid param index 3 (binary 11).
            WriteBits(bytes, ref bitOffset, value: 3u, bitCount: indexBits);

            component.ReceiveData("CHANGE_INDEX", bytes);

            component.StateMachine.Variables["A"].ShouldBeOfType<AnimBool>().Value.ShouldBeFalse();
            component.StateMachine.Variables["B"].ShouldBeOfType<AnimBool>().Value.ShouldBeTrue();
            component.StateMachine.Variables["C"].ShouldBeOfType<AnimBool>().Value.ShouldBeFalse();
        }

        [Test]
        public void ChangeCollisionPacket_DisambiguatesCollision_BySortedIndex()
        {
            // We need HashToName + collision buckets populated, which requires AnimStateMachine variable event wiring.
            var sm = CreateLinkedStateMachine();

            (string name1, string name2, ushort hash) = FindTwoDistinctNamesWithSameHash();

            // Ensure both start false.
            sm.Variables[name1] = new AnimBool(name1, false);
            sm.Variables[name2] = new AnimBool(name2, false);

            sm.HasAnyHashCollisions.ShouldBeTrue();
            sm.GetNamesForHash(hash).Count.ShouldBeGreaterThan(1);

            var component = new AnimStateMachineComponent { StateMachine = sm };

            // Collision index is based on SortedSet ordinal ordering.
            string[] ordered = new[] { name1, name2 }.OrderBy(n => n, StringComparer.Ordinal).ToArray();
            int targetIndex = Array.IndexOf(ordered, name2);
            targetIndex.ShouldBeGreaterThanOrEqualTo(0);

            int collisionIndexBits = AnimStateMachine.GetMinimalBitCountForCount(ordered.Length);
            collisionIndexBits.ShouldBe(1);

            int bitOffset = 0;
            int totalBits = 16 + 1 + collisionIndexBits + 1;
            var bytes = new byte[(totalBits + 7) / 8];

            WriteBits(bytes, ref bitOffset, hash, 16);
            WriteBits(bytes, ref bitOffset, value: 1u, bitCount: 1); // hasCollision
            WriteBits(bytes, ref bitOffset, value: (uint)targetIndex, bitCount: collisionIndexBits);
            WriteBits(bytes, ref bitOffset, value: 1u, bitCount: 1); // bool true

            component.ReceiveData("CHANGE_COLLISION", bytes);

            sm.Variables[name1].ShouldBeOfType<AnimBool>().Value.ShouldBeFalse();
            sm.Variables[name2].ShouldBeOfType<AnimBool>().Value.ShouldBeTrue();
        }

        private static AnimStateMachine CreateLinkedStateMachine()
        {
            var sm = new AnimStateMachine
            {
                Variables = new EventDictionary<string, AnimVar>()
            };
            return sm;
        }

        private static byte[] CreateSchemaPayload(int schemaVersion, IReadOnlyList<AnimStateMachine.AnimParameterSchemaEntry> entries)
        {
            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(schemaVersion);
                bw.Write((ushort)entries.Count);
                foreach (var entry in entries)
                {
                    byte[] nameBytes = Encoding.UTF8.GetBytes(entry.Name ?? string.Empty);
                    bw.Write((ushort)nameBytes.Length);
                    bw.Write(nameBytes);

                    bw.Write((byte)entry.Type);
                    switch (entry.Type)
                    {
                        case AnimStateMachine.AnimParameterType.Bool:
                            bw.Write(entry.BoolDefault);
                            break;
                        case AnimStateMachine.AnimParameterType.Int:
                            bw.Write(entry.IntDefault);
                            break;
                        case AnimStateMachine.AnimParameterType.Float:
                            bw.Write(entry.FloatDefault);
                            break;
                        default:
                            throw new InvalidOperationException($"Unsupported schema type: {entry.Type}");
                    }
                }
            }
            return ms.ToArray();
        }

        private static void WriteBits(byte[] data, ref int bitOffset, uint value, int bitCount)
        {
            for (int i = 0; i < bitCount; i++)
            {
                int byteIndex = bitOffset / 8;
                int bitIndex = bitOffset % 8;
                data[byteIndex] |= (byte)(((value >> i) & 1) << bitIndex);
                bitOffset++;
            }
        }

        private static void WriteBits(byte[] data, ref int bitOffset, ushort value, int bitCount)
            => WriteBits(data, ref bitOffset, (uint)value, bitCount);

        private static (string Name1, string Name2, ushort Hash) FindTwoDistinctNamesWithSameHash()
        {
            var seen = new Dictionary<ushort, string>();
            const int maxAttempts = 50000;

            for (int i = 0; i < maxAttempts; i++)
            {
                string name = $"Param_{i}";
                ushort hash = new AnimBool(name, false).Hash;

                if (seen.TryGetValue(hash, out var other) && !string.Equals(other, name, StringComparison.Ordinal))
                    return (other, name, hash);

                seen[hash] = name;
            }

            throw new AssertionException($"Failed to find a 16-bit hash collision within {maxAttempts} attempts.");
        }
    }
}
