namespace XREngine;

/// <summary>Native compositor results for the left and right eye submissions.</summary>
public readonly record struct RuntimeOpenVrSubmitResult(int LeftError, int RightError)
{
    public bool Succeeded => LeftError == 0 && RightError == 0;
}
