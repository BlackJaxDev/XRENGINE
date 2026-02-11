using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.Commands
{
    [Flags]
    public enum GPUViewFlags : uint
    {
        None = 0,
        StereoEyeLeft = 1u << 0,
        StereoEyeRight = 1u << 1,
        FullRes = 1u << 2,
        Foveated = 1u << 3,
        Mirror = 1u << 4,
        UsesSharedVisibility = 1u << 5
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GPUViewMask
    {
        public uint BitsLo;
        public uint BitsHi;

        public GPUViewMask(uint bitsLo, uint bitsHi)
        {
            BitsLo = bitsLo;
            BitsHi = bitsHi;
        }

        public static GPUViewMask AllVisible => new(uint.MaxValue, uint.MaxValue);

        public static GPUViewMask FromViewCount(uint viewCount)
        {
            if (viewCount == 0u)
                return new GPUViewMask(0u, 0u);

            if (viewCount >= 64u)
                return AllVisible;

            uint lo = viewCount >= 32u
                ? uint.MaxValue
                : ((1u << (int)viewCount) - 1u);

            uint hiCount = viewCount > 32u ? viewCount - 32u : 0u;
            uint hi = hiCount == 0u
                ? 0u
                : hiCount >= 32u
                    ? uint.MaxValue
                    : ((1u << (int)hiCount) - 1u);

            return new GPUViewMask(lo, hi);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GPUViewDescriptor
    {
        public uint ViewId;
        public uint ParentViewId;
        public uint Flags;
        public uint RenderPassMaskLo;

        public uint RenderPassMaskHi;
        public uint OutputLayer;
        public uint ViewRectX;
        public uint ViewRectY;

        public uint ViewRectW;
        public uint ViewRectH;
        public uint VisibleOffset;
        public uint VisibleCapacity;

        public Vector4 FoveationA;
        public Vector4 FoveationB;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GPUViewConstants
    {
        public Matrix4x4 View;
        public Matrix4x4 Projection;
        public Matrix4x4 ViewProjection;
        public Matrix4x4 PrevViewProjection;
        public Vector4 CameraPositionAndNear;
        public Vector4 CameraForwardAndFar;
    }

    public static class GPUViewSetBindings
    {
        public const int ViewDescriptorBuffer = 11;
        public const int ViewConstantsBuffer = 12;
        public const int CommandViewMaskBuffer = 13;
        public const int PerViewVisibleIndicesBuffer = 14;
        public const int PerViewDrawCountBuffer = 15;
    }

    public static class GPUViewSetLayout
    {
        public const uint InvalidViewId = uint.MaxValue;
        public const uint DefaultMaxViewCount = 8;
        public const uint AbsoluteMaxViewCount = 64;
        public const uint MaxPerViewVisibleIndexElements = 16u * 1024u * 1024u;

        public const uint ExpectedViewMaskSize = 8;
        public const uint ExpectedViewDescriptorSize = 80;
        public const uint ExpectedViewConstantsSize = 288;

        public static readonly uint ViewMaskSize = (uint)Marshal.SizeOf<GPUViewMask>();
        public static readonly uint ViewDescriptorSize = (uint)Marshal.SizeOf<GPUViewDescriptor>();
        public static readonly uint ViewConstantsSize = (uint)Marshal.SizeOf<GPUViewConstants>();

        public static uint ClampViewCount(uint value)
        {
            if (value == 0u)
                return 1u;

            return value > AbsoluteMaxViewCount ? AbsoluteMaxViewCount : value;
        }

        public static uint ComputePerViewVisibleCapacity(uint commandCapacity, uint viewCapacity)
        {
            if (commandCapacity == 0u || viewCapacity == 0u)
                return 1u;

            ulong requested = (ulong)commandCapacity * viewCapacity;
            ulong clamped = Math.Min(requested, MaxPerViewVisibleIndexElements);
            return (uint)Math.Max(1ul, clamped);
        }

        public static void ValidateRuntimeLayout()
        {
            if (ViewMaskSize != ExpectedViewMaskSize)
            {
                throw new InvalidOperationException(
                    $"GPUViewMask size mismatch. Expected {ExpectedViewMaskSize}, got {ViewMaskSize}.");
            }

            if (ViewDescriptorSize != ExpectedViewDescriptorSize)
            {
                throw new InvalidOperationException(
                    $"GPUViewDescriptor size mismatch. Expected {ExpectedViewDescriptorSize}, got {ViewDescriptorSize}.");
            }

            if (ViewConstantsSize != ExpectedViewConstantsSize)
            {
                throw new InvalidOperationException(
                    $"GPUViewConstants size mismatch. Expected {ExpectedViewConstantsSize}, got {ViewConstantsSize}.");
            }
        }
    }
}
