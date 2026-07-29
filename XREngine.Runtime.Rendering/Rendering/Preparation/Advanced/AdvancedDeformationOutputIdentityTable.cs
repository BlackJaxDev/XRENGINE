using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Stable output identity for a complete mesh/shared-pose pair.
/// </summary>
public sealed class AdvancedDeformationOutputIdentityTable
{
    private readonly AdvancedGpuHandle[] _meshes;
    private readonly AdvancedGpuHandle[] _poses;
    private readonly AdvancedGpuHandle[] _outputs;
    private uint _nextOutputIndex = 1u;

    public AdvancedDeformationOutputIdentityTable(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        int tableCapacity = NextPowerOfTwo(checked(capacity * 2));
        _meshes = new AdvancedGpuHandle[tableCapacity];
        _poses = new AdvancedGpuHandle[tableCapacity];
        _outputs = new AdvancedGpuHandle[tableCapacity];
        Capacity = capacity;
    }

    public int Capacity { get; }

    public bool TryGetOrAdd(
        AdvancedGpuHandle mesh,
        AdvancedGpuHandle pose,
        out AdvancedGpuHandle output)
    {
        if (!mesh.IsValid || !pose.IsValid)
            throw new ArgumentException("Output identity requires valid mesh and pose handles.");

        uint mask = checked((uint)_meshes.Length - 1u);
        uint start = Hash(mesh, pose) & mask;
        for (uint probe = 0u; probe < (uint)_meshes.Length; probe++)
        {
            int slot = checked((int)((start + probe) & mask));
            if (_meshes[slot] == mesh && _poses[slot] == pose)
            {
                output = _outputs[slot];
                return true;
            }
            if (_meshes[slot].IsValid)
                continue;
            if (_nextOutputIndex > (uint)Capacity)
                break;

            output = new AdvancedGpuHandle(_nextOutputIndex++, 1u);
            _meshes[slot] = mesh;
            _poses[slot] = pose;
            _outputs[slot] = output;
            return true;
        }

        output = AdvancedGpuHandle.Invalid;
        return false;
    }

    private static uint Hash(
        AdvancedGpuHandle mesh,
        AdvancedGpuHandle pose)
    {
        uint value = mesh.Index * 0x9E3779B9u;
        value ^= mesh.Generation * 0x85EBCA6Bu;
        value ^= pose.Index * 0xC2B2AE35u;
        value ^= pose.Generation * 0x27D4EB2Fu;
        value ^= value >> 16;
        return value;
    }

    private static int NextPowerOfTwo(int value)
    {
        int result = 1;
        while (result < value)
            result = checked(result << 1);
        return result;
    }
}
