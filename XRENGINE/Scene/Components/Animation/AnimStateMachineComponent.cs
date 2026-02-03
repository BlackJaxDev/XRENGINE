using Extensions;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Components.Animation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace XREngine.Components
{
    public class AnimStateMachineComponent : XRComponent
    {
        private const string ParamSchemaPacketId = "SCHEMA";
        private const string ChangeIndexedPacketId = "CHANGE_INDEX";
        private const string ChangeCollisionPacketId = "CHANGE_COLLISION";
        private const string ChangeHashPacketId = "CHANGE_HASH";

        private int _lastSentSchemaVersion = -1;

        private AnimStateMachine _stateMachine = new();
        public AnimStateMachine StateMachine
        {
            get => _stateMachine;
            set => SetField(ref _stateMachine, value);
        }

        private HumanoidComponent? _humanoid;
        public HumanoidComponent? Humanoid
        {
            get => _humanoid;
            set => SetField(ref _humanoid, value);
        }

        private bool _suspendedByClip;
        public bool SuspendedByClip
        {
            get => _suspendedByClip;
            private set => SetField(ref _suspendedByClip, value);
        }

        public void SetSuspendedByClip(bool suspended)
        {
            if (SuspendedByClip == suspended)
                return;

            SuspendedByClip = suspended;

            if (!IsActiveInHierarchy)
                return;

            if (suspended)
            {
                UnregisterTick(ETickGroup.Normal, ETickOrder.Animation, EvaluationTick);
            }
            else
            {
                RegisterTick(ETickGroup.Normal, ETickOrder.Animation, EvaluationTick);
            }
        }

        private HumanoidComponent? GetHumanoidComponent()
            => Humanoid ?? (TryGetSiblingComponent<HumanoidComponent>(out var humanoid) ? humanoid : null);

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            StateMachine.Initialize(this);
            StateMachine.VariableChanged += VariableChanged;
            if (!SuspendedByClip)
                RegisterTick(ETickGroup.Normal, ETickOrder.Animation, EvaluationTick);

            ReplicateParameterSchema(force: true);
        }

        private readonly HashSet<AnimVar> _changedLastEval = [];

        private void VariableChanged(AnimVar? var)
        {
            if (var is null)
                return;

            _changedLastEval.Add(var);
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            UnregisterTick(ETickGroup.Normal, ETickOrder.Animation, EvaluationTick);
            StateMachine.Deinitialize();
            StateMachine.VariableChanged -= VariableChanged;
        }

        protected internal void EvaluationTick()
        {
            if (SuspendedByClip)
                return;

            StateMachine.EvaluationTick(this, Engine.Delta);

            // Keep schema in sync before sending any indexed changes.
            ReplicateParameterSchema(force: false);
            ReplicateModifiedVariables();
            _changedLastEval.Clear();
        }

        private void ReplicateParameterSchema(bool force)
        {
            int schemaVersion = StateMachine.ParameterSchemaVersion;
            if (!force && schemaVersion == _lastSentSchemaVersion)
                return;

            var schema = StateMachine.GetOrderedParameterSchemaSnapshot();

            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(schemaVersion);
                bw.Write((ushort)schema.Count);
                for (int i = 0; i < schema.Count; i++)
                {
                    var entry = schema[i];
                    string name = entry.Name ?? string.Empty;
                    byte[] utf8 = Encoding.UTF8.GetBytes(name);
                    bw.Write((ushort)utf8.Length);
                    bw.Write(utf8);

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
                    }
                }
            }

            // The schema is needed for indexed replication; try to deliver reliably.
            EnqueueDataReplication(ParamSchemaPacketId, ms.ToArray(), compress: true, resendOnFailedAck: true);
            _lastSentSchemaVersion = schemaVersion;
        }

        private void ReplicateModifiedVariables()
        {
            int bitCount = 0;
            int indexBits = StateMachine.ParameterNameIdBitCount;
            bool canUseIndexFormat = indexBits < 16;
            bool useIndexFormat = canUseIndexFormat;

            if (useIndexFormat)
            {
                foreach (var variable in _changedLastEval)
                {
                    if (variable is null)
                        continue;
                    if (!StateMachine.TryGetParameterIndex(variable.ParameterName, out _))
                    {
                        useIndexFormat = false;
                        break;
                    }
                }
            }

            bool useHashedFormat = !StateMachine.HasAnyHashCollisions;
            foreach (var variable in _changedLastEval)
            {
                if (variable is null)
                    continue;

                if (useIndexFormat)
                {
                    bitCount += indexBits; // parameter id
                }
                else
                {
                    bitCount += 16; // hash
                    if (!useHashedFormat)
                    {
                        // Collision support: 1-bit flag + variable-length index when needed.
                        int collisionCount = StateMachine.GetNamesForHash(variable.Hash).Count;
                        bool hasCollision = collisionCount > 1;
                        bitCount += 1; // flag
                        if (hasCollision)
                            bitCount += GetCollisionIndexBitCount(collisionCount);
                    }
                }

                bitCount += variable.CalcBitCount();
            }
            if (bitCount == 0)
                return;

            byte[] data = new byte[bitCount.Align(8) / 8];
            int bitOffset = 0;
            foreach (var variable in _changedLastEval)
            {
                if (useIndexFormat)
                {
                    StateMachine.TryGetParameterIndex(variable!.ParameterName, out int paramId);
                    WriteBits(data, ref bitOffset, (uint)paramId, indexBits);
                }
                else
                {
                    ushort hash = variable!.Hash;
                    WriteBits(data, ref bitOffset, hash, 16);

                    if (!useHashedFormat)
                    {
                        int collisionCount = StateMachine.GetNamesForHash(hash).Count;
                        bool hasCollision = collisionCount > 1;
                        WriteBits(data, ref bitOffset, hasCollision ? 1u : 0u, 1);
                        if (hasCollision)
                        {
                            int collisionIndexBits = GetCollisionIndexBitCount(collisionCount);
                            int collisionIndex = GetCollisionIndex(hash, variable.ParameterName);
                            WriteBits(data, ref bitOffset, (uint)collisionIndex, collisionIndexBits);
                        }
                    }
                }
                variable?.WriteBits(data, ref bitOffset);
            }

            string packetId = useIndexFormat 
                ? ChangeIndexedPacketId
                : (useHashedFormat 
                    ? ChangeHashPacketId
                    : ChangeCollisionPacketId);
            
            EnqueueDataReplication(packetId, data, false, false);
        }

        private static int GetCollisionIndexBitCount(int collisionCount)
        {
            return AnimStateMachine.GetMinimalBitCountForCount(collisionCount);
        }

        private int GetCollisionIndex(ushort hash, string name)
        {
            var names = StateMachine.GetNamesForHash(hash);
            if (names.Count <= 1)
                return 0;

            int i = 0;
            foreach (var n in names)
            {
                if (string.Equals(n, name, StringComparison.Ordinal))
                    return i;
                i++;
            }

            return 0;
        }

        private static string? GetCollisionNameByIndex(IReadOnlyCollection<string> names, int index)
        {
            if (names.Count <= 1)
            {
                foreach (var n in names)
                    return n;
                return null;
            }

            if (index < 0)
                return null;

            int i = 0;
            foreach (var n in names)
            {
                if (i == index)
                    return n;
                i++;
            }
            return null;
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

        private static uint ReadBits(byte[] bytes, ref int bitOffset, int bitCount)
        {
            uint value = 0;
            for (int i = 0; i < bitCount; i++)
            {
                int byteIndex = bitOffset / 8;
                int bitIndex = bitOffset % 8;
                value |= (uint)(((bytes[byteIndex] >> bitIndex) & 1) << i);
                bitOffset++;
            }
            return value;
        }

        public override void ReceiveData(string id, object? data)
        {
            if (data is not byte[] bytes || bytes.Length == 0)
                return;

            if (id == ParamSchemaPacketId)
            {
                try
                {
                    using var ms = new MemoryStream(bytes);
                    using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: false);

                    int schemaVersion = br.ReadInt32();
                    int count = br.ReadUInt16();
                    var entries = new List<AnimStateMachine.AnimParameterSchemaEntry>(count);
                    for (int i = 0; i < count; i++)
                    {
                        int len = br.ReadUInt16();
                        byte[] nameBytes = br.ReadBytes(len);

                        string name = Encoding.UTF8.GetString(nameBytes);
                        var type = (AnimStateMachine.AnimParameterType)br.ReadByte();

                        bool boolDefault = false;
                        int intDefault = 0;
                        float floatDefault = 0f;
                        switch (type)
                        {
                            case AnimStateMachine.AnimParameterType.Bool:
                                boolDefault = br.ReadBoolean();
                                break;
                            case AnimStateMachine.AnimParameterType.Int:
                                intDefault = br.ReadInt32();
                                break;
                            case AnimStateMachine.AnimParameterType.Float:
                                floatDefault = br.ReadSingle();
                                break;
                            default:
                                // Unknown type; abort the schema.
                                return;
                        }

                        entries.Add(new AnimStateMachine.AnimParameterSchemaEntry(name, type, boolDefault, intDefault, floatDefault));
                    }

                    StateMachine.ApplyReplicatedParameterSchema(entries, schemaVersion);
                    // Prevent immediately echoing the same schema back.
                    _lastSentSchemaVersion = schemaVersion;
                }
                catch
                {
                    // Ignore malformed schema payloads.
                }
                return;
            }

            int bitOffset = 0;
            switch (id)
            {
                case ChangeIndexedPacketId: //[paramId:indexBits][valueBits...]
                {
                    int indexBits = StateMachine.ParameterNameIdBitCount;
                    while (bitOffset + indexBits <= bytes.Length * 8)
                    {
                        int paramIndex = indexBits == 0 
                            ? 0 
                            : (int)ReadBits(bytes, ref bitOffset, indexBits);

                        if (!StateMachine.TryGetParameterNameByIndex(paramIndex, out var varName) || 
                            varName is null || 
                            !StateMachine.Variables.TryGetValue(varName, out var animVar) || 
                            animVar is null)
                            break;

                        animVar.ReadBits(bytes, ref bitOffset);
                    }
                    return;
                }
                case ChangeHashPacketId: //[hash:16][valueBits...]
                {
                    while (bitOffset + 16 <= bytes.Length * 8)
                    {
                        ushort hash = (ushort)ReadBits(bytes, ref bitOffset, 16);

                        if (StateMachine.HashToName.TryGetValue(hash, out var varName) &&
                            StateMachine.Variables.TryGetValue(varName, out var animVar))
                            animVar.ReadBits(bytes, ref bitOffset);
                    }
                    return;
                }
                case ChangeCollisionPacketId: //[hash:16][hasCollision:1][collisionIndex:?][valueBits...]
                {
                    while (bitOffset + 17 <= bytes.Length * 8)
                    {
                        ushort hash = (ushort)ReadBits(bytes, ref bitOffset, 16);
                        bool hasCollision = ReadBits(bytes, ref bitOffset, 1) != 0;

                        string? varName;
                        if (!hasCollision)
                        {
                            StateMachine.HashToName.TryGetValue(hash, out varName);
                        }
                        else
                        {
                            var names = StateMachine.GetNamesForHash(hash);
                            int collisionCount = names.Count;
                            if (collisionCount <= 1)
                                continue;

                            int indexBits = GetCollisionIndexBitCount(collisionCount);
                            if (bitOffset + indexBits > bytes.Length * 8)
                                break;

                            int collisionIndex = (int)ReadBits(bytes, ref bitOffset, indexBits);
                            varName = GetCollisionNameByIndex(names, collisionIndex);
                        }

                        if (varName is null)
                            continue;

                        if (StateMachine.Variables.TryGetValue(varName, out var animVar))
                            animVar.ReadBits(bytes, ref bitOffset);
                    }
                    break;
                }
            }
        }

        public void SetFloat(string name, float value)
        {
            var sm = StateMachine;
            if (sm.Variables.TryGetValue(name, out var variable))
                variable.FloatValue = value;
        }
        public void SetInt(string name, int value)
        {
            var sm = StateMachine;
            if (sm.Variables.TryGetValue(name, out var variable))
                variable.IntValue = value;
        }
        public void SetBool(string name, bool value)
        {
            var sm = StateMachine;
            if (sm.Variables.TryGetValue(name, out var variable))
                variable.BoolValue = value;
        }

        public void SetHumanoidValue(EHumanoidValue name, float value)
            => GetHumanoidComponent()?.SetValue(name, value);
    }
}
