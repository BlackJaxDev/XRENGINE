using System.Numerics;
using System.Runtime.InteropServices.JavaScript;

namespace XREngine.Browser;

public sealed partial class BrowserSceneSession
{
    public void SetSceneLabel(string label)
    {
        ThrowIfFrameBusy();
        ArgumentNullException.ThrowIfNull(label);
        label = label.Trim();
        if (label.Length is < 1 or > 64)
            throw new ArgumentException("Scene label requires 1–64 characters.", nameof(label));
        foreach (char value in label)
            if (char.IsControl(value)) throw new ArgumentException("Scene label cannot contain control characters.", nameof(label));
        _parent.Name = label;
    }

    private void PublishAudioListener()
    {
        Vector3 position = MotionPosition;
        Vector3 forward = MotionForward;
        Vector3 up = Vector3.UnitY;
        if (_snapshot is not null && Matrix4x4.Invert(_left.View, out Matrix4x4 inverse))
        {
            position = inverse.Translation;
            forward = Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, inverse));
            up = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, inverse));
        }
        PublishBrowserListener(Id, position.X, position.Y, position.Z,
            forward.X, forward.Y, forward.Z, up.X, up.Y, up.Z);
    }

    [JSImport("updateListener", "xrengine.audio")]
    private static partial void PublishBrowserListener(int session, float x, float y, float z,
        float forwardX, float forwardY, float forwardZ, float upX, float upY, float upZ);
}
