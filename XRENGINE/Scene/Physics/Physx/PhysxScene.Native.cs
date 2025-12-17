using System.Runtime.InteropServices;

namespace XREngine.Rendering.Physics.Physx
{
    public unsafe partial class PhysxScene
    {
        public class Native
        {
            public static void* CreateVTable<T1>(T1? d1) where T1 : Delegate
            {
                int count = 1;
                nint vtblPtr = Marshal.AllocHGlobal(nint.Size * count);
                for (int i = 0; i < 1; i++)
                {
                    nint offset = nint.Add(vtblPtr, nint.Size * i);
                    var funcPtr = i switch
                    {
                        0 => d1 is null ? nint.Zero : Marshal.GetFunctionPointerForDelegate(d1),
                        _ => nint.Zero,
                    };
                    Marshal.WriteIntPtr(offset, funcPtr);
                }
                GC.KeepAlive(d1);
                return (void*)vtblPtr;
            }
            public static void* CreateVTable<T1, T2>(T1? d1, T2? d2) where T1 : Delegate where T2 : Delegate
            {
                int count = 2;
                nint vtblPtr = Marshal.AllocHGlobal(nint.Size * count);
                for (int i = 0; i < 2; i++)
                {
                    nint offset = nint.Add(vtblPtr, nint.Size * i);
                    var funcPtr = i switch
                    {
                        0 => d1 is null ? nint.Zero : Marshal.GetFunctionPointerForDelegate(d1),
                        1 => d2 is null ? nint.Zero : Marshal.GetFunctionPointerForDelegate(d2),
                        _ => nint.Zero,
                    };
                    Marshal.WriteIntPtr(offset, funcPtr);
                }
                GC.KeepAlive(d1);
                GC.KeepAlive(d2);
                return (void*)vtblPtr;
            }
            public static void* CreateVTable<T1, T2, T3>(T1? d1, T2? d2, T3? d3) where T1 : Delegate where T2 : Delegate where T3 : Delegate
            {
                int count = 3;
                nint vtblPtr = Marshal.AllocHGlobal(nint.Size * count);
                for (int i = 0; i < 3; i++)
                {
                    nint offset = nint.Add(vtblPtr, nint.Size * i);
                    var funcPtr = i switch
                    {
                        0 => d1 is null ? nint.Zero : Marshal.GetFunctionPointerForDelegate(d1),
                        1 => d2 is null ? nint.Zero : Marshal.GetFunctionPointerForDelegate(d2),
                        2 => d3 is null ? nint.Zero : Marshal.GetFunctionPointerForDelegate(d3),
                        _ => nint.Zero,
                    };
                    Marshal.WriteIntPtr(offset, funcPtr);
                }
                GC.KeepAlive(d1);
                GC.KeepAlive(d2);
                GC.KeepAlive(d3);
                return (void*)vtblPtr;
            }
            public static void* CreateVTable<T1, T2, T3, T4>(T1? d1, T2? d2, T3? d3, T4? d4) where T1 : Delegate where T2 : Delegate where T3 : Delegate where T4 : Delegate
            {
                int count = 4;
                nint vtblPtr = Marshal.AllocHGlobal(nint.Size * count);
                for (int i = 0; i < 4; i++)
                {
                    nint offset = nint.Add(vtblPtr, nint.Size * i);
                    var funcPtr = i switch
                    {
                        0 => d1 is null ? nint.Zero : Marshal.GetFunctionPointerForDelegate(d1),
                        1 => d2 is null ? nint.Zero : Marshal.GetFunctionPointerForDelegate(d2),
                        2 => d3 is null ? nint.Zero : Marshal.GetFunctionPointerForDelegate(d3),
                        3 => d4 is null ? nint.Zero : Marshal.GetFunctionPointerForDelegate(d4),
                        _ => nint.Zero,
                    };
                    Marshal.WriteIntPtr(offset, funcPtr);
                }
                GC.KeepAlive(d1);
                GC.KeepAlive(d2);
                GC.KeepAlive(d3);
                GC.KeepAlive(d4);
                return (void*)vtblPtr;
            }
            public static void* CreateVTable(nint d1, nint d2, nint d3, nint d4)
            {
                int count = 4;
                nint vtblPtr = Marshal.AllocHGlobal(nint.Size * count);
                for (int i = 0; i < 4; i++)
                {
                    nint offset = nint.Add(vtblPtr, nint.Size * i);
                    var funcPtr = i switch
                    {
                        0 => d1,
                        1 => d2,
                        2 => d3,
                        3 => d4,
                        _ => nint.Zero,
                    };
                    Marshal.WriteIntPtr(offset, funcPtr);
                }
                return (void*)vtblPtr;
            }
            public static void* CreateVTable<T1, T2, T3, T4, T5>(T1? d1, T2? d2, T3? d3, T4? d4, T5? d5) where T1 : Delegate where T2 : Delegate where T3 : Delegate where T4 : Delegate where T5 : Delegate
            {
                int count = 5;
                nint vtblPtr = Marshal.AllocHGlobal(nint.Size * count);
                for (int i = 0; i < 5; i++)
                {
                    nint offset = nint.Add(vtblPtr, nint.Size * i);
                    var funcPtr = i switch
                    {
                        0 => d1 is null ? nint.Zero : Marshal.GetFunctionPointerForDelegate(d1),
                        1 => d2 is null ? nint.Zero : Marshal.GetFunctionPointerForDelegate(d2),
                        2 => d3 is null ? nint.Zero : Marshal.GetFunctionPointerForDelegate(d3),
                        3 => d4 is null ? nint.Zero : Marshal.GetFunctionPointerForDelegate(d4),
                        4 => d5 is null ? nint.Zero : Marshal.GetFunctionPointerForDelegate(d5),
                        _ => nint.Zero,
                    };
                    Marshal.WriteIntPtr(offset, funcPtr);
                }
                GC.KeepAlive(d1);
                GC.KeepAlive(d2);
                GC.KeepAlive(d3);
                GC.KeepAlive(d4);
                GC.KeepAlive(d5);
                return (void*)vtblPtr;
            }
            public static void* CreateVTable<T1, T2, T3, T4, T5, T6>(T1? d1, T2? d2, T3? d3, T4? d4, T5? d5, T6? d6) where T1 : Delegate where T2 : Delegate where T3 : Delegate where T4 : Delegate where T5 : Delegate where T6 : Delegate
            {
                int count = 6;
                nint vtblPtr = Marshal.AllocHGlobal(nint.Size * count);
                for (int i = 0; i < 6; i++)
                {
                    nint offset = nint.Add(vtblPtr, nint.Size * i);
                    var funcPtr = i switch
                    {
                        0 => d1 is null ? nint.Zero : Marshal.GetFunctionPointerForDelegate(d1),
                        1 => d2 is null ? nint.Zero : Marshal.GetFunctionPointerForDelegate(d2),
                        2 => d3 is null ? nint.Zero : Marshal.GetFunctionPointerForDelegate(d3),
                        3 => d4 is null ? nint.Zero : Marshal.GetFunctionPointerForDelegate(d4),
                        4 => d5 is null ? nint.Zero : Marshal.GetFunctionPointerForDelegate(d5),
                        5 => d6 is null ? nint.Zero : Marshal.GetFunctionPointerForDelegate(d6),
                        _ => nint.Zero,
                    };
                    Marshal.WriteIntPtr(offset, funcPtr);
                }
                GC.KeepAlive(d1);
                GC.KeepAlive(d2);
                GC.KeepAlive(d3);
                GC.KeepAlive(d4);
                GC.KeepAlive(d5);
                GC.KeepAlive(d6);
                return (void*)vtblPtr;
            }
            public static void* CreateVTable<T1, T2, T3, T4, T5, T6, T7>(T1? d1, T2? d2, T3? d3, T4? d4, T5? d5, T6? d6, T7? d7) where T1 : Delegate where T2 : Delegate where T3 : Delegate where T4 : Delegate where T5 : Delegate where T6 : Delegate where T7 : Delegate
            {
                int count = 7;
                nint vtblPtr = Marshal.AllocHGlobal(nint.Size * count);
                for (int i = 0; i < 7; i++)
                {
                    nint offset = nint.Add(vtblPtr, nint.Size * i);
                    var funcPtr = i switch
                    {
                        0 => d1 is null ? nint.Zero : Marshal.GetFunctionPointerForDelegate(d1),
                        1 => d2 is null ? nint.Zero : Marshal.GetFunctionPointerForDelegate(d2),
                        2 => d3 is null ? nint.Zero : Marshal.GetFunctionPointerForDelegate(d3),
                        3 => d4 is null ? nint.Zero : Marshal.GetFunctionPointerForDelegate(d4),
                        4 => d5 is null ? nint.Zero : Marshal.GetFunctionPointerForDelegate(d5),
                        5 => d6 is null ? nint.Zero : Marshal.GetFunctionPointerForDelegate(d6),
                        6 => d7 is null ? nint.Zero : Marshal.GetFunctionPointerForDelegate(d7),
                        _ => nint.Zero,
                    };
                    Marshal.WriteIntPtr(offset, funcPtr);
                }
                GC.KeepAlive(d1);
                GC.KeepAlive(d2);
                GC.KeepAlive(d3);
                GC.KeepAlive(d4);
                GC.KeepAlive(d5);
                GC.KeepAlive(d6);
                GC.KeepAlive(d7);
                return (void*)vtblPtr;
            }

            public static void FreeVTable(void* vtable)
                => Marshal.FreeHGlobal((nint)vtable);
        }
    }
}