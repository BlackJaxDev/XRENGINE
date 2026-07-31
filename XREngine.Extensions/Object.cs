using System.Reflection;

namespace XREngine.Extensions
{
    public static class ObjectExtensions
    {
        public static object? CallPrivateMethod(this object o, string methodName, params object[] args)
            => o?.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(o, args);
        public static bool Is<T>(this object o)
            => o is T;
        public static bool IsNot<T>(this object o)
            => o is not T;
        public static bool IsNull(this object o)
             => o is null;
        public static bool IsNotNull(this object o)
            => o is not null;
    }

    public static class BooleanExtensions
    {
        public static bool IsTrue(this bool b)
            => b is true;
        public static bool IsFalse(this bool b)
            => b is false;
        public static bool IsTrue(this bool? b)
            => b is true;
        public static bool IsFalse(this bool? b)
            => b is false;
        public static bool Negated(this bool b)
            => !b;
        public static void IfTrue(this bool b, Action action)
        {
            if (b) action();
        }
        public static void IfFalse(this bool b, Action action)
        {
            if (!b) action();
        }
        public static void IfTrue(this bool? b, Action action)
        {
            if (b is true) action();
        }
        public static void IfFalse(this bool? b, Action action)
        {
            if (b is false) action();
        }
        public static void IfTrue(this bool b, Action action, Action elseAction)
        {
            if (b) action();
            else elseAction();
        }
        public static void IfFalse(this bool b, Action action, Action elseAction)
        {
            if (!b) action();
            else elseAction();
        }
        public static void IfTrue(this bool? b, Action action, Action elseAction)
        {
            if (b is true) action();
            else elseAction();
        }
        public static void IfFalse(this bool? b, Action action, Action elseAction)
        {
            if (b is false) action();
            else elseAction();
        }
        public static void SetFlag<T>(this bool b, ref T value, T flag) where T : struct, Enum
        {
            if (b)
                value = (T)(object)((int)(object)value | (int)(object)flag);
            else
                value = (T)(object)((int)(object)value & ~(int)(object)flag);
        }
        public static void ClearFlag<T>(this bool b, ref T value, T flag) where T : struct, Enum
        {
            if (b)
                value = (T)(object)((int)(object)value & ~(int)(object)flag);
            else
                value = (T)(object)((int)(object)value | (int)(object)flag);
        }
        public static void SetFlag(this bool b, ref sbyte value, sbyte flag)
        {
            if (b)
                value |= flag;
            else
                value &= (sbyte)~flag;
        }
        public static void ClearFlag(this bool b, ref sbyte value, sbyte flag)
        {
            if (b)
                value &= (sbyte)~flag;
            else
                value |= flag;
        }
        public static void SetFlag(this bool b, ref byte value, byte flag)
        {
            if (b)
                value |= flag;
            else
                value &= (byte)~flag;
        }
        public static void ClearFlag(this bool b, ref byte value, byte flag)
        {
            if (b)
                value &= (byte)~flag;
            else
                value |= flag;
        }
        public static void SetFlag(this bool b, ref short value, short flag)
        {
            if (b)
                value |= flag;
            else
                value &= (short)~flag;
        }
        public static void ClearFlag(this bool b, ref short value, short flag)
        {
            if (b)
                value &= (short)~flag;
            else
                value |= flag;
        }
        public static void SetFlag(this bool b, ref ushort value, ushort flag)
        {
            if (b)
                value |= flag;
            else
                value &= (ushort)~flag;
        }
        public static void ClearFlag(this bool b, ref ushort value, ushort flag)
        {
            if (b)
                value &= (ushort)~flag;
            else
                value |= flag;
        }
        public static void SetFlag(this bool b, ref int value, int flag)
        {
            if (b)
                value |= flag;
            else
                value &= ~flag;
        }
        public static void ClearFlag(this bool b, ref int value, int flag)
        {
            if (b)
                value &= ~flag;
            else
                value |= flag;
        }
        public static void SetFlag(this bool b, ref uint value, uint flag)
        {
            if (b)
                value |= flag;
            else
                value &= ~flag;
        }
        public static void ClearFlag(this bool b, ref uint value, uint flag)
        {
            if (b)
                value &= ~flag;
            else
                value |= flag;
        }
        public static void SetFlag(this bool b, ref long value, long flag)
        {
            if (b)
                value |= flag;
            else
                value &= ~flag;
        }
        public static void ClearFlag(this bool b, ref long value, long flag)
        {
            if (b)
                value &= ~flag;
            else
                value |= flag;
        }
        public static void SetFlag(this bool b, ref ulong value, ulong flag)
        {
            if (b)
                value |= flag;
            else
                value &= ~flag;
        }
        public static void ClearFlag(this bool b, ref ulong value, ulong flag)
        {
            if (b)
                value &= ~flag;
            else
                value |= flag;
        }
        public static void SetFlagAtBit(this bool b, ref sbyte value, int bit)
        {
            if (b)
                value |= (sbyte)(1 << bit);
            else
                value &= (sbyte)~(1 << bit);
        }
        public static void ClearFlagAtBit(this bool b, ref sbyte value, int bit)
        {
            if (b)
                value &= (sbyte)~(1 << bit);
            else
                value |= (sbyte)(1 << bit);
        }
        public static void SetFlagAtBit(this bool b, ref byte value, int bit)
        {
            if (b)
                value |= (byte)(1 << bit);
            else
                value &= (byte)~(1 << bit);
        }
        public static void ClearFlagAtBit(this bool b, ref byte value, int bit)
        {
            if (b)
                value &= (byte)~(1 << bit);
            else
                value |= (byte)(1 << bit);
        }
        public static void SetFlagAtBit(this bool b, ref short value, int bit)
        {
            if (b)
                value |= (short)(1 << bit);
            else
                value &= (short)~(1 << bit);
        }
        public static void ClearFlagAtBit(this bool b, ref short value, int bit)
        {
            if (b)
                value &= (short)~(1 << bit);
            else
                value |= (short)(1 << bit);
        }
        public static void SetFlagAtBit(this bool b, ref ushort value, int bit)
        {
            if (b)
                value |= (ushort)(1 << bit);
            else
                value &= (ushort)~(1 << bit);
        }
        public static void ClearFlagAtBit(this bool b, ref ushort value, int bit)
        {
            if (b)
                value &= (ushort)~(1 << bit);
            else
                value |= (ushort)(1 << bit);
        }
        public static void SetFlagAtBit(this bool b, ref int value, int bit)
        {
            if (b)
                value |= (1 << bit);
            else
                value &= ~(1 << bit);
        }
        public static void ClearFlagAtBit(this bool b, ref int value, int bit)
        {
            if (b)
                value &= ~(1 << bit);
            else
                value |= (1 << bit);
        }
        public static void SetFlagAtBit(this bool b, ref uint value, int bit)
        {
            if (b)
                value |= (1u << bit);
            else
                value &= ~(1u << bit);
        }
        public static void ClearFlagAtBit(this bool b, ref uint value, int bit)
        {
            if (b)
                value &= ~(1u << bit);
            else
                value |= (1u << bit);
        }
        public static void SetFlagAtBit(this bool b, ref long value, int bit)
        {
            if (b)
                value |= (1L << bit);
            else
                value &= ~(1L << bit);
        }
        public static void ClearFlagAtBit(this bool b, ref long value, int bit)
        {
            if (b)
                value &= ~(1L << bit);
            else
                value |= (1L << bit);
        }
        public static void SetFlagAtBit(this bool b, ref ulong value, int bit)
        {
            if (b)
                value |= (1UL << bit);
            else
                value &= ~(1UL << bit);
        }
        public static void ClearFlagAtBit(this bool b, ref ulong value, int bit)
        {
            if (b)
                value &= ~(1UL << bit);
            else
                value |= (1UL << bit);
        }
    }
}
