using System.Numerics;
using System.Text.Json;

namespace XREngine.Browser;

public sealed partial class BrowserSceneSession
{
    private string? _registeredCookedCollisionId;
    private string? _installedCookedCollisionId;
    private BrowserKinematicCharacter? _registeredCookedCharacter;
    private float _registeredCookedYaw;
    private float _registeredCookedPitch;

    public bool HasCookedCollision => _installedCookedCollisionId is not null;
    public string? InstalledCookedCollisionId => _installedCookedCollisionId;
    public int CookedCollisionBoxCount { get; private set; }

    private void UploadCookedCollision(string assetId, byte[] payload)
    {
        if (_registeredCookedCollisionId is not null)
            throw new ArgumentException("Only one cooked collision world is permitted per session.");
        BrowserCookedCollisionDto dto = JsonSerializer.Deserialize(payload,
            BrowserCookedJsonContext.Default.BrowserCookedCollisionDto)
            ?? throw new ArgumentException("Cooked collision payload is null.");
        if (dto.Boxes is not { Length: >= 1 and <= 64 } || dto.Spawn is not { Length: 3 } ||
            !float.IsFinite(dto.Yaw) || dto.Yaw < -MathF.PI || dto.Yaw > MathF.PI ||
            !float.IsFinite(dto.Pitch) || dto.Pitch is < -1.4f or > 1.4f)
            throw new ArgumentException("Cooked collision world or initial orientation is invalid.");

        long retainedBytes = dto.Boxes.Length * 24L; // The character owns a cloned box array.
        ReserveCookedBytes(retainedBytes);
        BrowserCollisionBox[] boxes = new BrowserCollisionBox[dto.Boxes.Length];
        for (int i = 0; i < boxes.Length; i++)
        {
            BrowserCookedCollisionBoxDto box = dto.Boxes[i]
                ?? throw new ArgumentException("Cooked collision box is null.");
            boxes[i] = new BrowserCollisionBox(CookedCollisionVector(box.Minimum),
                CookedCollisionVector(box.Maximum));
        }
        // Constructor enforces finite bounded extents and spawn clearance against
        // the same expanded AABBs used by the character sweep.
        BrowserKinematicCharacter character = new(CookedCollisionVector(dto.Spawn), boxes);
        _registeredCookedCharacter = character;
        _registeredCookedCollisionId = assetId;
        _registeredCookedYaw = dto.Yaw;
        _registeredCookedPitch = dto.Pitch;
        CookedCollisionBoxCount = boxes.Length;
        _cookedRetainedBytes += retainedBytes;
        _peakCookedScratchBytes = Math.Max(_peakCookedScratchBytes, retainedBytes * 3 + 12);
    }

    private static Vector3 CookedCollisionVector(float[]? values)
    {
        if (values is not { Length: 3 })
            throw new ArgumentException("Cooked collision vector requires three components.");
        return new Vector3(values[0], values[1], values[2]);
    }

    private void ValidateCookedCollisionReference(string? assetId)
    {
        if (assetId is null)
        {
            if (_installedCookedCollisionId is not null)
                throw new ArgumentException("Cooked scene chunks must retain their collision world.");
            return;
        }
        ValidateCookedId(assetId);
        if (_cookedSceneChunks != 0 && _installedCookedCollisionId is null)
            throw new ArgumentException("Cooked scene chunks must retain their initial collision policy.");
        if (_registeredCookedCollisionId != assetId || _registeredCookedCharacter is null ||
            (_installedCookedCollisionId is not null && _installedCookedCollisionId != assetId))
            throw new ArgumentException("Cooked scene collision dependency is not resident or does not match.");
    }

    private void InstallCookedCollision(string assetId)
    {
        ValidateCookedCollisionReference(assetId);
        if (_installedCookedCollisionId is not null)
            return;
        _character = _registeredCookedCharacter;
        _yaw = _registeredCookedYaw;
        _pitch = _registeredCookedPitch;
        _lookDelta = default;
        _jumpQueued = false;
        _installedCookedCollisionId = assetId;
        RebuildViews();
    }

    private void ClearCookedCollision()
    {
        _registeredCookedCharacter = null;
        _registeredCookedCollisionId = null;
        _installedCookedCollisionId = null;
        CookedCollisionBoxCount = 0;
    }
}
