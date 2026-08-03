using XREngine.Scene.Physics.Physx;
using ImageMagick;
using MagicPhysX;
using System.Numerics;
using XREngine.Data;
using static MagicPhysX.NativeMethods;

namespace XREngine.Scene.Physics.Physx
{
    public unsafe class PhysxHeightField : PhysxBase
    {
        public PxHeightField* HeightFieldPtr { get; }
        public override unsafe PxBase* BasePtr => (PxBase*)HeightFieldPtr;

        public PxHeightFieldFlags Flags => HeightFieldPtr->GetFlags();
        public unsafe uint RowCount => HeightFieldPtr->GetNbRows();
        public unsafe uint ColumnCount => HeightFieldPtr->GetNbColumns();
        public unsafe PxHeightFieldFormat Format => HeightFieldPtr->GetFormat();
        public unsafe uint SampleStride => HeightFieldPtr->GetSampleStride();
        public unsafe float ConvexEdgeThreshold => HeightFieldPtr->GetConvexEdgeThreshold();

        public PhysxHeightField(PxHeightField* heightFieldPtr)
        {
            HeightFieldPtr = heightFieldPtr;
        }
        public PhysxHeightField(string imagePath)
        {
            using MagickImage image = new(imagePath);
            uint width = image.Width;
            uint height = image.Height;
            long sampleCount = checked((long)width * height);
            if (sampleCount > int.MaxValue)
                throw new NotSupportedException("PhysX heightfields cannot contain more than 2,147,483,647 samples.");

            using IPixelCollection<float> pixels = image.GetPixels()
                ?? throw new InvalidDataException("Image does not contain pixel values.");
            ushort[] values = pixels.ToShortArray("I")
                ?? throw new InvalidDataException("ImageMagick returned no heightfield samples.");
            if (values.Length != sampleCount)
                throw new InvalidDataException("Image size does not match heightfield sample count.");

            PxHeightFieldSample[] samples = GC.AllocateUninitializedArray<PxHeightFieldSample>(values.Length);
            for (int index = 0; index < values.Length; index++)
            {
                ref PxHeightFieldSample sample = ref samples[index];
                sample.height = unchecked((short)(values[index] - 32768));
                if (index % 2 != 0)
                    sample.SetTessFlagMut();
                else
                    sample.ClearTessFlagMut();
            }

            PxHeightFieldDesc desc = PxHeightFieldDesc_new();
            desc.nbColumns = width;
            desc.nbRows = height;
            desc.samples.stride = (uint)sizeof(PxHeightFieldSample);
            desc.format = PxHeightFieldFormat.S16Tm;
            //desc.convexEdgeThreshold = 3.0f;
            //desc.flags = PxHeightFieldFlags.NoBoundaryEdges;

            fixed (PxHeightFieldSample* samplePtr = samples)
            {
                desc.samples.data = samplePtr;
                HeightFieldPtr = phys_PxCreateHeightField(&desc, PhysxScene.PhysicsPtr->GetPhysicsInsertionCallbackMut());
            }

            if (HeightFieldPtr is null)
                throw new InvalidOperationException($"PhysX failed to create a heightfield from '{imagePath}'.");
        }

        public uint SaveCells(DataSource data)
            => HeightFieldPtr->SaveCells(data.Address, data.Length);

        public void Release()
            => HeightFieldPtr->ReleaseMut();

        public unsafe bool ModifySamplesMut(int startCol, int startRow, PxHeightFieldDesc* subfieldDesc, bool shrinkBounds)
            => HeightFieldPtr->ModifySamplesMut(startCol, startRow, subfieldDesc, shrinkBounds);

        public unsafe float GetHeight(float x, float z)
            => HeightFieldPtr->GetHeight(x, z);

        public unsafe ushort GetTriangleMaterialIndex(uint triangleIndex)
            => HeightFieldPtr->GetTriangleMaterialIndex(triangleIndex);

        public unsafe Vector3 GetTriangleNormal(uint triangleIndex)
            => HeightFieldPtr->GetTriangleNormal(triangleIndex);

        public unsafe PxHeightFieldSample* GetSample(uint row, uint column)
            => HeightFieldPtr->GetSample(row, column);

        public unsafe uint GetTimestamp()
            => HeightFieldPtr->GetTimestamp();

        public PxHeightFieldGeometry NewGeometry(
            float heightScale = 1.0f,
            float rowScale = 1.0f,
            float columnScale = 1.0f,
            bool tightBounds = false,
            bool doubleSided = false)
        {
            PxMeshGeometryFlags flags = 0;
            if (tightBounds)
                flags |= PxMeshGeometryFlags.TightBounds;
            if (doubleSided)
                flags |= PxMeshGeometryFlags.DoubleSided;
            return PxHeightFieldGeometry_new(HeightFieldPtr, flags, heightScale, rowScale, columnScale);
        }
    }
}
