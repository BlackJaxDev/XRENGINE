using ImageMagick;
using MagicPhysX;
using System.Numerics;
using XREngine.Data;
using static MagicPhysX.NativeMethods;

namespace XREngine.Rendering.Physics.Physx
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
            MagickImage image = new(imagePath);
            uint width = image.Width;
            uint height = image.Height;
            var samples = stackalloc PxHeightFieldSample[(int)(width * height)];

            var values = image.GetPixels().GetValues() ?? throw new Exception("Image does not contain pixel values.");
            if (values.Length != width * height)
                throw new Exception("Image size does not match heightfield size.");

            Parallel.For(0, values.Length, index =>
            {
                PxHeightFieldSample* sample = &samples[index];
                sample->height = (short)values[index];
                if (index % 2 != 0)
                    sample->SetTessFlagMut();
                else
                    sample->ClearTessFlagMut();
            });

            PxHeightFieldDesc desc = PxHeightFieldDesc_new();
            desc.nbColumns = width;
            desc.nbRows = height;
            desc.samples.data = samples;
            desc.samples.stride = (uint)sizeof(PxHeightFieldSample);
            desc.format = PxHeightFieldFormat.S16Tm;
            //desc.convexEdgeThreshold = 3.0f;
            //desc.flags = PxHeightFieldFlags.NoBoundaryEdges;

            HeightFieldPtr = phys_PxCreateHeightField(&desc, PhysxScene.PhysicsPtr->GetPhysicsInsertionCallbackMut());
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