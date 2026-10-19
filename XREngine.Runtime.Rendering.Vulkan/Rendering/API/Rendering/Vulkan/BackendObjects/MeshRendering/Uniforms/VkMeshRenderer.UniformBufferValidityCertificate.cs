using System;
using System.Collections.Generic;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
    private readonly Dictionary<string, UniformBufferValidityCertificate> _engineUniformBufferCertificates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UniformBufferValidityCertificate> _autoUniformBufferCertificates = new(StringComparer.Ordinal);

    // This certificate covers array structure. A selected frequency view must
    // still acquire its exact allocation before a write or native recording.
    private struct UniformBufferValidityCertificate(
        Array source,
        uint minimumSize,
        bool allHandlesValid,
        bool allMapped)
    {
        public readonly Array Source = source;
        public uint MinimumSize = minimumSize;
        public bool AllHandlesValid = allHandlesValid;
        public bool AllMapped = allMapped;

        public readonly bool Matches(Array source, int expectedCount, uint requiredSize, bool requireMappedPointers)
            => ReferenceEquals(Source, source) &&
               source.Length == expectedCount &&
               AllHandlesValid &&
               MinimumSize >= requiredSize &&
               (!requireMappedPointers || AllMapped);

        public void Include(AutoUniformBuffer buffer)
        {
            MinimumSize = Math.Min(MinimumSize, buffer.Size);
            AllHandlesValid &= buffer.Buffer.Handle != 0;
            AllMapped &= buffer.UsesMappedFrameArena || buffer.UsesMappedMemoryLease;
        }
    }

    private bool EngineUniformBuffersValid(string name, EngineUniformBuffer[] buffers, int expectedCount, uint requiredSize)
    {
        if (!_engineUniformBufferCertificates.TryGetValue(name, out UniformBufferValidityCertificate certificate) ||
            !ReferenceEquals(certificate.Source, buffers))
        {
            uint minimumSize = uint.MaxValue;
            bool allHandlesValid = true;
            bool allMapped = true;
            foreach (EngineUniformBuffer buffer in buffers)
            {
                minimumSize = Math.Min(minimumSize, buffer.Size);
                allHandlesValid &= buffer.Buffer.Handle != 0;
                allMapped &= buffer.UsesMappedFrameArena || buffer.UsesMappedMemoryLease;
            }
            certificate = new UniformBufferValidityCertificate(buffers, minimumSize, allHandlesValid, allMapped);
            _engineUniformBufferCertificates[name] = certificate;
        }

        return certificate.Matches(buffers, expectedCount, requiredSize, requireMappedPointers: true);
    }

    private bool AutoUniformBuffersValid(
        string name,
        AutoUniformBuffer[] buffers,
        int expectedCount,
        uint requiredSize,
        bool requireMappedPointers)
    {
        if (!_autoUniformBufferCertificates.TryGetValue(name, out UniformBufferValidityCertificate certificate) ||
            !ReferenceEquals(certificate.Source, buffers))
        {
            uint minimumSize = uint.MaxValue;
            bool allHandlesValid = true;
            bool allMapped = true;
            foreach (AutoUniformBuffer buffer in buffers)
            {
                minimumSize = Math.Min(minimumSize, buffer.Size);
                allHandlesValid &= buffer.Buffer.Handle != 0;
                allMapped &= buffer.UsesMappedFrameArena || buffer.UsesMappedMemoryLease;
            }
            certificate = new UniformBufferValidityCertificate(buffers, minimumSize, allHandlesValid, allMapped);
            _autoUniformBufferCertificates[name] = certificate;
        }

        return certificate.Matches(buffers, expectedCount, requiredSize, requireMappedPointers);
    }

    private void IncludeAutoUniformBufferReplacement(string name, AutoUniformBuffer[] buffers, AutoUniformBuffer replacement)
    {
        if (_autoUniformBufferCertificates.TryGetValue(name, out UniformBufferValidityCertificate certificate) &&
            ReferenceEquals(certificate.Source, buffers))
        {
            certificate.Include(replacement);
            _autoUniformBufferCertificates[name] = certificate;
        }
    }
}
