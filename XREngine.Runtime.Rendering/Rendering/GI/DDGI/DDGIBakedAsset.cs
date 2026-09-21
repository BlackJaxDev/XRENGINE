using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Components.Lights;
using XREngine.Data;
using XREngine.Data.Vectors;
using XREngine.Rendering;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.GI.DDGI
{
    /// <summary>
    /// Serialized container representing a pre-baked DDGI volume.
    /// Stores probe grid parameters, probe relocation states, and compressed/raw octahedral
    /// irradiance and visibility atlas data across cascade layers.
    /// Skips probe tracing while retaining atlas visibility sampling for a stationary volume.
    /// </summary>
    public sealed class DDGIBakedAsset
    {
        public const uint AssetMagic = 0x49474444u; // "DDGI" in little-endian ASCII
        // Version 2 uses directional distance moments with the narrow visibility
        // filter. The old moments cannot be corrected without tracing the scene.
        public const uint AssetVersion = 2u;

        public string VolumeName { get; set; } = string.Empty;
        public IVector3 ProbeCounts { get; set; } = DDGIVolumeRuntimeState.DefaultProbeCounts;
        public Vector3 HalfExtents { get; set; } = new(10.0f, 6.0f, 10.0f);
        public Vector3 Origin { get; set; } = Vector3.Zero;
        public int CascadeCount { get; set; } = 1;
        public float CascadeSpacingMultiplier { get; set; } = 2.0f;
        public int IrradianceAtlasWidth { get; set; } = (int)DDGIVolumeRuntimeState.DefaultIrradianceAtlasWidth;
        public int IrradianceAtlasHeight { get; set; } = (int)DDGIVolumeRuntimeState.DefaultIrradianceAtlasHeight;
        public int VisibilityAtlasWidth { get; set; } = (int)DDGIVolumeRuntimeState.DefaultVisibilityAtlasWidth;
        public int VisibilityAtlasHeight { get; set; } = (int)DDGIVolumeRuntimeState.DefaultVisibilityAtlasHeight;
        public float NormalBias { get; set; } = 0.1f;
        public float ViewBias { get; set; } = 0.2f;
        public float ChebyshevPower { get; set; } = 4.0f;
        public float Intensity { get; set; } = 1.0f;

        public DDGIProbeGPU[] Probes { get; set; } = Array.Empty<DDGIProbeGPU>();
        public byte[][] IrradianceAtlasLayers { get; set; } = Array.Empty<byte[]>();
        public byte[][] VisibilityAtlasLayers { get; set; } = Array.Empty<byte[]>();

        public int TotalProbeCount => checked(ProbeCounts.X * ProbeCounts.Y * ProbeCounts.Z);

        /// <summary>
        /// Captures a completed DDGI GPU state for an explicit user-requested bake. This is a synchronous,
        /// cold-path readback and must never be called from per-frame rendering.
        /// </summary>
        public static bool TryCaptureFromGpu(
            DDGIVolumeRuntimeState state,
            string volumeName,
            XRTexture irradianceAtlas,
            XRTexture visibilityAtlas,
            XRDataBuffer probeBuffer,
            out DDGIBakedAsset? asset,
            out string failure)
        {
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(irradianceAtlas);
            ArgumentNullException.ThrowIfNull(visibilityAtlas);
            ArgumentNullException.ThrowIfNull(probeBuffer);

            asset = null;
            if (AbstractRenderer.Current is not IBufferDiagnosticReadbackBackendCapability bufferReadback)
            {
                failure = "DDGI baking requires an active renderer with synchronous diagnostic buffer readback support.";
                return false;
            }

            int probeCount;
            try
            {
                probeCount = checked(state.TotalProbeCount * state.CascadeCount);
            }
            catch (OverflowException)
            {
                failure = "DDGI baked probe count exceeds the supported signed 32-bit range.";
                return false;
            }

            if (probeCount <= 0)
            {
                failure = "DDGI baking requires at least one configured probe.";
                return false;
            }

            var probes = new DDGIProbeGPU[probeCount];
            if (!bufferReadback.TryReadBufferBytes(probeBuffer, 0u, MemoryMarshal.AsBytes(probes.AsSpan()), out string probeRoute))
            {
                failure = $"DDGI probe-state readback failed through '{probeRoute}'.";
                return false;
            }

            if (!TryCaptureAtlasLayers(irradianceAtlas, state.CascadeCount, state.IrradianceAtlasWidth, state.IrradianceAtlasHeight,
                    EPixelFormat.Rgb, EPixelType.Float, out byte[][] irradianceLayers, out failure) ||
                !TryCaptureAtlasLayers(visibilityAtlas, state.CascadeCount, state.VisibilityAtlasWidth, state.VisibilityAtlasHeight,
                    EPixelFormat.Rg, EPixelType.HalfFloat, out byte[][] visibilityLayers, out failure))
            {
                return false;
            }

            asset = state.CaptureBakedAsset(volumeName, irradianceLayers, visibilityLayers, probes);
            return true;
        }

        /// <summary>
        /// Serializes this baked DDGI asset into a binary stream.
        /// </summary>
        public void Save(Stream stream)
        {
            ValidatePayload();
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            writer.Write(AssetMagic);
            writer.Write(AssetVersion);

            writer.Write(VolumeName ?? string.Empty);
            writer.Write(ProbeCounts.X);
            writer.Write(ProbeCounts.Y);
            writer.Write(ProbeCounts.Z);

            writer.Write(HalfExtents.X);
            writer.Write(HalfExtents.Y);
            writer.Write(HalfExtents.Z);

            writer.Write(Origin.X);
            writer.Write(Origin.Y);
            writer.Write(Origin.Z);

            writer.Write(CascadeCount);
            writer.Write(CascadeSpacingMultiplier);

            writer.Write(IrradianceAtlasWidth);
            writer.Write(IrradianceAtlasHeight);
            writer.Write(VisibilityAtlasWidth);
            writer.Write(VisibilityAtlasHeight);

            writer.Write(NormalBias);
            writer.Write(ViewBias);
            writer.Write(ChebyshevPower);
            writer.Write(Intensity);

            // Probe state buffer
            int probeCount = Probes?.Length ?? 0;
            writer.Write(probeCount);
            for (int i = 0; i < probeCount; i++)
            {
                var p = Probes![i];
                writer.Write(p.Position.X);
                writer.Write(p.Position.Y);
                writer.Write(p.Position.Z);
                writer.Write(p.Position.W);

                writer.Write(p.RelocationOffset.X);
                writer.Write(p.RelocationOffset.Y);
                writer.Write(p.RelocationOffset.Z);
                writer.Write(p.RelocationOffset.W);
            }

            // Irradiance atlas layers
            int irrLayerCount = IrradianceAtlasLayers?.Length ?? 0;
            writer.Write(irrLayerCount);
            for (int k = 0; k < irrLayerCount; k++)
            {
                byte[] layerBytes = IrradianceAtlasLayers![k] ?? Array.Empty<byte>();
                writer.Write(layerBytes.Length);
                if (layerBytes.Length > 0)
                {
                    writer.Write(layerBytes);
                }
            }

            // Visibility atlas layers
            int visLayerCount = VisibilityAtlasLayers?.Length ?? 0;
            writer.Write(visLayerCount);
            for (int k = 0; k < visLayerCount; k++)
            {
                byte[] layerBytes = VisibilityAtlasLayers![k] ?? Array.Empty<byte>();
                writer.Write(layerBytes.Length);
                if (layerBytes.Length > 0)
                {
                    writer.Write(layerBytes);
                }
            }
        }

        /// <summary>
        /// Saves this baked DDGI asset to a file.
        /// </summary>
        public void Save(string filePath)
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            using var fs = File.Create(filePath);
            Save(fs);
        }

        /// <summary>
        /// Deserializes a baked DDGI asset from a binary stream.
        /// </summary>
        public static DDGIBakedAsset Load(Stream stream)
        {
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            uint magic = reader.ReadUInt32();
            if (magic != AssetMagic)
            {
                throw exciting(new InvalidDataException($"Invalid DDGI baked asset magic: 0x{magic:X8}, expected 0x{AssetMagic:X8}"));
            }

            uint version = reader.ReadUInt32();
            if (version != AssetVersion)
            {
                throw exciting(new InvalidDataException(
                    version == 1u
                        ? "DDGI baked asset version 1 uses obsolete visibility filtering. Re-capture the volume in Dynamic mode and save a new bake."
                        : $"Unsupported DDGI baked asset version: {version}, supported version: {AssetVersion}"));
            }

            var asset = new DDGIBakedAsset
            {
                VolumeName = reader.ReadString(),
                ProbeCounts = new IVector3(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()),
                HalfExtents = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                Origin = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                CascadeCount = reader.ReadInt32(),
                CascadeSpacingMultiplier = reader.ReadSingle(),
                IrradianceAtlasWidth = reader.ReadInt32(),
                IrradianceAtlasHeight = reader.ReadInt32(),
                VisibilityAtlasWidth = reader.ReadInt32(),
                VisibilityAtlasHeight = reader.ReadInt32(),
                NormalBias = reader.ReadSingle(),
                ViewBias = reader.ReadSingle(),
                ChebyshevPower = reader.ReadSingle(),
                Intensity = reader.ReadSingle(),
            };

            asset.ValidateMetadata();
            int probeCount = ReadExpectedCount(reader, checked(asset.TotalProbeCount * asset.CascadeCount), "probe");
            asset.Probes = new DDGIProbeGPU[probeCount];
            for (int i = 0; i < probeCount; i++)
            {
                asset.Probes[i] = new DDGIProbeGPU
                {
                    Position = new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    RelocationOffset = new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                };
            }

            int irrLayerCount = ReadExpectedCount(reader, asset.CascadeCount, "irradiance layer");
            asset.IrradianceAtlasLayers = new byte[irrLayerCount][];
            for (int k = 0; k < irrLayerCount; k++)
            {
                int len = ReadExpectedCount(reader, checked(asset.IrradianceAtlasWidth * asset.IrradianceAtlasHeight * 12), "irradiance byte");
                asset.IrradianceAtlasLayers[k] = reader.ReadBytes(len);
                if (asset.IrradianceAtlasLayers[k].Length != len)
                    throw new EndOfStreamException("DDGI irradiance payload is truncated.");
            }

            int visLayerCount = ReadExpectedCount(reader, asset.CascadeCount, "visibility layer");
            asset.VisibilityAtlasLayers = new byte[visLayerCount][];
            for (int k = 0; k < visLayerCount; k++)
            {
                int len = ReadExpectedCount(reader, checked(asset.VisibilityAtlasWidth * asset.VisibilityAtlasHeight * 4), "visibility byte");
                asset.VisibilityAtlasLayers[k] = reader.ReadBytes(len);
                if (asset.VisibilityAtlasLayers[k].Length != len)
                    throw new EndOfStreamException("DDGI visibility payload is truncated.");
            }

            asset.ValidatePayload();
            return asset;
        }

        /// <summary>
        /// Loads a baked DDGI asset from a file.
        /// </summary>
        public static DDGIBakedAsset Load(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Baked DDGI asset file not found: {filePath}", filePath);
            }
            using var fs = File.OpenRead(filePath);
            return Load(fs);
        }

        /// <summary>
        /// Applies the properties and configuration stored in this baked asset to a target DDGIVolumeComponent.
        /// Configures the volume into Baked update mode.
        /// </summary>
        public void ApplyTo(DDGIVolumeComponent volume)
        {
            ArgumentNullException.ThrowIfNull(volume);
            ValidatePayload();
            ApplyLayoutTo(volume);
            if (volume.NormalBias != NormalBias)
                volume.NormalBias = NormalBias;
            if (volume.ViewBias != ViewBias)
                volume.ViewBias = ViewBias;
            if (volume.ChebyshevPower != ChebyshevPower)
                volume.ChebyshevPower = ChebyshevPower;
            if (volume.Intensity != Intensity)
                volume.Intensity = Intensity;
            if (!ReferenceEquals(volume.BakedAsset, this))
                volume.BakedAsset = this;

            volume.RuntimeState.Synchronize(volume);
        }

        /// <summary>
        /// Reapplies the immutable spatial layout required for this baked payload
        /// without changing live shading and presentation controls.
        /// </summary>
        public void ApplyLayoutTo(DDGIVolumeComponent volume)
        {
            ArgumentNullException.ThrowIfNull(volume);
            ValidateMetadata();
            if (volume.Transform.WorldTranslation != Origin)
            {
                if (volume.Transform is not Transform transform)
                    throw new InvalidOperationException("Applying a baked DDGI origin requires a standard writable Transform.");
                transform.SetWorldTranslation(Origin);
            }
            IVector3 currentCounts = volume.ProbeCounts;
            if (currentCounts.X != ProbeCounts.X || currentCounts.Y != ProbeCounts.Y || currentCounts.Z != ProbeCounts.Z)
                volume.ProbeCounts = ProbeCounts;
            if (volume.HalfExtents != HalfExtents)
                volume.HalfExtents = HalfExtents;
            if (volume.CascadeCount != CascadeCount)
                volume.CascadeCount = CascadeCount;
            if (volume.CascadeSpacingMultiplier != CascadeSpacingMultiplier)
                volume.CascadeSpacingMultiplier = CascadeSpacingMultiplier;
            if (volume.CameraScrolling)
                volume.CameraScrolling = false;
            if (volume.DisableCoarseVisibility)
                volume.DisableCoarseVisibility = false;
            if (volume.UpdateMode != EDDGIUpdateMode.Baked)
                volume.UpdateMode = EDDGIUpdateMode.Baked;
        }

        /// <summary>
        /// Uploads probe state buffer data and atlas texture data to GPU resources.
        /// </summary>
        public void UploadToGpu(XRTexture? irradianceAtlas, XRTexture? visibilityAtlas, XRDataBuffer? probeBuffer)
        {
            ValidatePayload();
            if (probeBuffer is null)
                throw new InvalidOperationException("Cannot upload a baked DDGI asset without a probe-state buffer.");
            if (irradianceAtlas is not XRTexture2DArray irrArray)
                throw new InvalidOperationException("Cannot upload a baked DDGI asset without an irradiance texture array.");
            if (visibilityAtlas is not XRTexture2DArray visArray)
                throw new InvalidOperationException("Cannot upload a baked DDGI asset without a visibility texture array.");

            ValidateUploadDestination(probeBuffer, irrArray, visArray);
            probeBuffer.SetDataArrayRawAtIndex(0u, Probes);
            probeBuffer.PushData();
            UploadAtlasLayers(irrArray, IrradianceAtlasLayers);
            UploadAtlasLayers(visArray, VisibilityAtlasLayers);

            // The array object owns backend storage. Invalidating child mipmaps alone does not
            // schedule the parent-array transfer consumed by the Vulkan backend.
            irrArray.PushData();
            visArray.PushData();
        }

        private static bool TryCaptureAtlasLayers(XRTexture texture, int cascadeCount, int expectedWidth, int expectedHeight,
            EPixelFormat expectedFormat, EPixelType expectedType, out byte[][] layers, out string failure)
        {
            layers = Array.Empty<byte[]>();
            if (texture is not XRTexture2DArray array)
            {
                failure = "DDGI baking requires texture-array atlas resources.";
                return false;
            }
            if (cascadeCount < 1 || array.Textures.Length < cascadeCount)
            {
                failure = $"DDGI atlas has {array.Textures.Length} layers but bake requires {cascadeCount}.";
                return false;
            }

            layers = new byte[cascadeCount][];
            for (int layer = 0; layer < cascadeCount; layer++)
            {
                XRTexture2D slice = array.Textures[layer];
                if (slice.Width != (uint)expectedWidth || slice.Height != (uint)expectedHeight || slice.Mipmaps is not [var mip, ..] ||
                    mip.PixelFormat != expectedFormat || mip.PixelType != expectedType)
                {
                    failure = $"DDGI atlas layer {layer} does not match its configured {expectedWidth}x{expectedHeight} {expectedFormat}/{expectedType} layout.";
                    layers = Array.Empty<byte[]>();
                    return false;
                }

                string readFailure = "No active renderer.";
                AbstractRenderer? renderer = AbstractRenderer.Current;
                if (renderer is null ||
                    !renderer.TryReadTextureMipRgbaFloat(array, 0, layer, out float[]? rgba, out int width, out int height, out readFailure) ||
                    rgba is null || width != expectedWidth || height != expectedHeight)
                {
                    failure = $"DDGI atlas layer {layer} readback failed: {readFailure}";
                    layers = Array.Empty<byte[]>();
                    return false;
                }

                try
                {
                    layers[layer] = EncodeReadbackLayer(rgba, width, height, expectedFormat, expectedType);
                }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or OverflowException)
                {
                    failure = $"DDGI atlas layer {layer} could not be encoded for baked upload: {ex.Message}";
                    layers = Array.Empty<byte[]>();
                    return false;
                }
            }

            failure = string.Empty;
            return true;
        }

        private static byte[] EncodeReadbackLayer(float[] rgba, int width, int height, EPixelFormat format, EPixelType type)
        {
            int channelCount = format switch
            {
                EPixelFormat.Rg => 2,
                EPixelFormat.Rgb => 3,
                EPixelFormat.Rgba => 4,
                _ => throw new NotSupportedException($"DDGI baked capture does not support {format} atlas data.")
            };
            int bytesPerChannel = type switch
            {
                EPixelType.HalfFloat => sizeof(ushort),
                EPixelType.Float => sizeof(float),
                _ => throw new NotSupportedException($"DDGI baked capture does not support {type} atlas data.")
            };
            int pixelCount = checked(width * height);
            if (rgba.Length != checked(pixelCount * 4))
                throw new InvalidDataException($"DDGI atlas readback supplied {rgba.Length} floats for a {width}x{height} RGBA image.");

            byte[] encoded = new byte[checked(pixelCount * channelCount * bytesPerChannel)];
            for (int pixel = 0; pixel < pixelCount; pixel++)
            {
                for (int channel = 0; channel < channelCount; channel++)
                {
                    float value = rgba[(pixel * 4) + channel];
                    if (!float.IsFinite(value))
                        throw new InvalidDataException("DDGI atlas readback contains a non-finite value.");
                    int offset = (pixel * channelCount + channel) * bytesPerChannel;
                    if (type == EPixelType.Float)
                        BitConverter.TryWriteBytes(encoded.AsSpan(offset, sizeof(float)), value);
                    else
                        BitConverter.TryWriteBytes(encoded.AsSpan(offset, sizeof(ushort)), BitConverter.HalfToUInt16Bits((Half)value));
                }
            }

            return encoded;
        }

        private void ValidatePayload()
        {
            ValidateMetadata();

            int expectedProbeCount = checked(TotalProbeCount * CascadeCount);
            if (Probes is null || Probes.Length != expectedProbeCount)
                throw new InvalidDataException($"DDGI baked asset has {Probes?.Length ?? 0} probes; expected {expectedProbeCount}.");
            for (int i = 0; i < Probes.Length; i++)
                if (!IsFinite(Probes[i].Position) || !IsFinite(Probes[i].RelocationOffset))
                    throw new InvalidDataException($"DDGI baked probe {i} contains a non-finite value.");

            ValidateAtlasPayload(IrradianceAtlasLayers, CascadeCount, IrradianceAtlasWidth, IrradianceAtlasHeight, 3 * sizeof(float), "irradiance");
            ValidateAtlasPayload(VisibilityAtlasLayers, CascadeCount, VisibilityAtlasWidth, VisibilityAtlasHeight, 2 * sizeof(ushort), "visibility");
        }

        private void ValidateMetadata()
        {
            if (ProbeCounts.X < 1 || ProbeCounts.Y < 1 || ProbeCounts.Z < 1)
                throw new InvalidDataException("DDGI baked assets require at least one probe on every axis.");
            if (CascadeCount is < 1 or > (int)DDGIVolumeRuntimeState.DefaultMaxCascades)
                throw new InvalidDataException($"DDGI baked asset cascade count must be between one and {DDGIVolumeRuntimeState.DefaultMaxCascades}.");
            if (!IsFinite(Origin) || !IsFinite(HalfExtents) || HalfExtents.X <= 0 || HalfExtents.Y <= 0 || HalfExtents.Z <= 0 ||
                !float.IsFinite(CascadeSpacingMultiplier) || CascadeSpacingMultiplier < 1 ||
                !float.IsFinite(NormalBias) || NormalBias < 0 || !float.IsFinite(ViewBias) || ViewBias < 0 ||
                !float.IsFinite(ChebyshevPower) || ChebyshevPower <= 0 || !float.IsFinite(Intensity) || Intensity < 0)
                throw new InvalidDataException("DDGI baked volume parameters must be finite and within their supported ranges.");
            var dimensions = new DDGIResourceDescriptor(ProbeCounts, 1, CascadeCount, 1);
            try
            {
                _ = DDGIResourceDescriptor.FromVariant(dimensions.ToVariant());
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidDataException("DDGI baked volume exceeds the supported resource limits.", ex);
            }
            if (IrradianceAtlasWidth != dimensions.IrradianceWidth || IrradianceAtlasHeight != dimensions.IrradianceHeight ||
                VisibilityAtlasWidth != dimensions.VisibilityWidth || VisibilityAtlasHeight != dimensions.VisibilityHeight)
                throw new InvalidDataException("DDGI baked atlas dimensions do not match the probe grid.");
        }

        private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
        private static bool IsFinite(Vector4 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);

        private static int ReadExpectedCount(BinaryReader reader, int expected, string name)
        {
            int actual = reader.ReadInt32();
            return actual == expected ? actual : throw new InvalidDataException($"DDGI baked {name} count is {actual}; expected {expected}.");
        }

        private static void ValidateAtlasPayload(byte[][]? layers, int cascadeCount, int width, int height, int bytesPerPixel, string name)
        {
            if (layers is null || layers.Length != cascadeCount)
                throw new InvalidDataException($"DDGI baked asset has {layers?.Length ?? 0} {name} layers; expected {cascadeCount}.");

            int expectedLength = checked(width * height * bytesPerPixel);
            for (int layer = 0; layer < layers.Length; layer++)
                if (layers[layer] is null || layers[layer].Length != expectedLength)
                    throw new InvalidDataException($"DDGI baked {name} layer {layer} has {layers[layer]?.Length ?? 0} bytes; expected {expectedLength}.");
        }

        private void ValidateUploadDestination(XRDataBuffer probeBuffer, XRTexture2DArray irradianceAtlas, XRTexture2DArray visibilityAtlas)
        {
            uint expectedProbeBytes = checked((uint)(Probes.Length * Marshal.SizeOf<DDGIProbeGPU>()));
            if (probeBuffer.Length < expectedProbeBytes)
                throw new InvalidOperationException($"DDGI probe-state buffer is {probeBuffer.Length} bytes; baked asset requires {expectedProbeBytes} bytes.");

            ValidateAtlasDestination(irradianceAtlas, IrradianceAtlasWidth, IrradianceAtlasHeight, EPixelFormat.Rgb, EPixelType.Float, "irradiance");
            ValidateAtlasDestination(visibilityAtlas, VisibilityAtlasWidth, VisibilityAtlasHeight, EPixelFormat.Rg, EPixelType.HalfFloat, "visibility");
        }

        private void ValidateAtlasDestination(XRTexture2DArray atlas, int width, int height, EPixelFormat pixelFormat, EPixelType pixelType, string name)
        {
            if (atlas.Textures.Length < CascadeCount)
                throw new InvalidOperationException($"DDGI {name} atlas has {atlas.Textures.Length} layers; baked asset requires {CascadeCount}.");

            for (int layer = 0; layer < CascadeCount; layer++)
            {
                XRTexture2D texture = atlas.Textures[layer];
                if (texture.Width != (uint)width || texture.Height != (uint)height || texture.Mipmaps is not [var mip, ..] ||
                    mip.PixelFormat != pixelFormat || mip.PixelType != pixelType)
                {
                    throw new InvalidOperationException($"DDGI {name} atlas layer {layer} does not match baked asset dimensions or upload format.");
                }
            }
        }

        private static void UploadAtlasLayers(XRTexture2DArray atlas, byte[][] layers)
        {
            for (int layer = 0; layer < layers.Length; layer++)
            {
                Mipmap2D mip = atlas.Textures[layer].Mipmaps![0];
                mip.Data = new DataSource(layers[layer]);
                mip.Invalidate();
            }
        }

        private static Exception exciting(Exception ex) => ex;
    }
}
