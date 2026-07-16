namespace XREngine.Rendering
{
public static partial class GpuDispatchLogger
    {
        /// <summary>
        /// Log categories for GPU dispatch debugging.
        /// </summary>
        [Flags]
        public enum LogCategory
        {
            None = 0,
            /// <summary>Lifecycle events (init, dispose, render begin/end)</summary>
            Lifecycle = 1 << 0,
            /// <summary>Buffer operations (create, bind, map, unmap)</summary>
            Buffers = 1 << 1,
            /// <summary>Culling operations (frustum, BVH, occlusion)</summary>
            Culling = 1 << 2,
            /// <summary>Sorting operations (material sort, distance sort)</summary>
            Sorting = 1 << 3,
            /// <summary>Indirect command building</summary>
            Indirect = 1 << 4,
            /// <summary>Material batching and resolution</summary>
            Materials = 1 << 5,
            /// <summary>Statistics and metrics</summary>
            Stats = 1 << 6,
            /// <summary>Draw dispatch calls</summary>
            Draw = 1 << 7,
            /// <summary>VAO/attribute configuration</summary>
            VAO = 1 << 8,
            /// <summary>Shader program binding</summary>
            Shaders = 1 << 9,
            /// <summary>Uniform setting</summary>
            Uniforms = 1 << 10,
            /// <summary>Memory barriers and synchronization</summary>
            Sync = 1 << 11,
            /// <summary>Errors and warnings</summary>
            Errors = 1 << 12,
            /// <summary>Performance timing</summary>
            Timing = 1 << 13,
            /// <summary>Validation checks</summary>
            Validation = 1 << 14,
            /// <summary>State transitions</summary>
            State = 1 << 15,
            /// <summary>All categories enabled</summary>
            All = ~0
        }
    }
}
