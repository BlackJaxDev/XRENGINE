using System.Numerics;

namespace XREngine.Animation.MotionBricks;

/// <summary>
/// Controls output token length bounds, heading alignment, and endpoint correction
/// for a SOMA MotionBricks inference call.
/// </summary>
public readonly record struct SomaInferenceConfig
{
    /// <summary>
    /// Default allowed token bitmask enabling 8 to 14 tokens (26 to 50 valid frames at 30 Hz),
    /// matching NVIDIA MotionBricks <c>DEFAULT_TOKEN_COUNTS</c>.
    /// </summary>
    public static readonly int DefaultAllowedTokenBits = CreateAllowedTokenBits(8, 14);

    public static SomaInferenceConfig Default { get; } = new(
        DefaultAllowedTokenBits,
        correctGeneratedDiff: true,
        headingAlignment: new Vector2(1.0f, 0.0f));

    /// <summary>
    /// Bitmask of allowed token counts from 6 to 16, where bit 0 corresponds to 6 tokens.
    /// </summary>
    public int AllowedTokenBits { get; }

    /// <summary>
    /// When true, applies endpoint correction so frame 0 matches the last context pose
    /// and the final frame matches the first target pose.
    /// </summary>
    public bool CorrectGeneratedDiff { get; }

    /// <summary>
    /// Ground-plane direction <c>(x, z)</c> in SOMA model space that the last context pose
    /// is rotated to face before inference. Defaults to <c>(1, 0)</c> (+X).
    /// </summary>
    public Vector2 HeadingAlignment { get; }

    public SomaInferenceConfig(int allowedTokenBits, bool correctGeneratedDiff, Vector2 headingAlignment)
    {
        const int validMask = (1 << SomaModelContract.AllowedTokenSlotCount) - 1;
        if ((allowedTokenBits & validMask) == 0 || (allowedTokenBits & ~validMask) != 0)
            throw new ArgumentOutOfRangeException(
                nameof(allowedTokenBits),
                allowedTokenBits,
                "AllowedTokenBits must enable at least one token count between 6 and 16.");

        if (!float.IsFinite(headingAlignment.X)
            || !float.IsFinite(headingAlignment.Y)
            || headingAlignment.LengthSquared() <= 1.0e-12f)
        {
            throw new ArgumentException("HeadingAlignment must be a finite non-zero 2D direction.", nameof(headingAlignment));
        }

        AllowedTokenBits = allowedTokenBits;
        CorrectGeneratedDiff = correctGeneratedDiff;
        HeadingAlignment = headingAlignment;
    }

    /// <summary>
    /// Builds a token bitmask enabling all token counts in the inclusive range <c>[minTokens, maxTokens]</c>.
    /// </summary>
    public static int CreateAllowedTokenBits(int minTokens, int maxTokens)
    {
        if (minTokens < SomaModelContract.MinTokenCount
            || maxTokens > SomaModelContract.MaxTokenCount
            || minTokens > maxTokens)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minTokens),
                $"Allowed token range must lie within [{SomaModelContract.MinTokenCount}, {SomaModelContract.MaxTokenCount}].");
        }

        int bits = 0;
        for (int token = minTokens; token <= maxTokens; token++)
            bits |= 1 << (token - SomaModelContract.MinTokenCount);
        return bits;
    }

    /// <summary>
    /// Checks whether a predicted token count is permitted by <see cref="AllowedTokenBits"/>.
    /// </summary>
    public bool IsTokenCountAllowed(int tokenCount)
    {
        if (tokenCount < SomaModelContract.MinTokenCount || tokenCount > SomaModelContract.MaxTokenCount)
            return false;

        int bit = 1 << (tokenCount - SomaModelContract.MinTokenCount);
        return (AllowedTokenBits & bit) != 0;
    }
}
