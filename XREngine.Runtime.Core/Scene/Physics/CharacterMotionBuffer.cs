using System.Numerics;

namespace XREngine.Scene.Physics;

/// <summary>
/// Fixed-capacity, allocation-free handoff from movement-command producers to the
/// fixed-step native character controller.
/// </summary>
internal sealed class CharacterMotionBuffer(int capacity = 64)
{
    private const float DurationEpsilon = 1e-7f;

    private readonly object _sync = new();
    private readonly CharacterMotionCommand[] _commands = new CharacterMotionCommand[
        Math.Max(2, capacity)];
    private int _head;
    private int _count;

    public bool Enqueue(in CharacterMotionCommand command)
    {
        if (!IsFinite(command.Value)
            || !float.IsFinite(command.MinDistance)
            || !float.IsFinite(command.ElapsedTime)
            || command.ElapsedTime <= DurationEpsilon)
            return false;

        CharacterMotionCommand sanitized = command with
        {
            MinDistance = MathF.Max(0.0f, command.MinDistance),
        };

        lock (_sync)
        {
            if (_count == _commands.Length)
            {
                int tail = (_head + _count - 1) % _commands.Length;
                _commands[tail] = Merge(_commands[tail], sanitized);
                return true;
            }

            int index = (_head + _count) % _commands.Length;
            _commands[index] = sanitized;
            _count++;
            return true;
        }
    }

    public CharacterMotionStep Consume(float fixedDelta)
    {
        if (!float.IsFinite(fixedDelta) || fixedDelta <= DurationEpsilon)
            return default;

        lock (_sync)
        {
            float remainingStepTime = fixedDelta;
            float consumedTime = 0.0f;
            float minimumDistance = 0.0f;
            Vector3 displacement = Vector3.Zero;

            while (_count > 0 && remainingStepTime > DurationEpsilon)
            {
                CharacterMotionCommand command = _commands[_head];
                float consumedCommandTime = MathF.Min(command.ElapsedTime, remainingStepTime);
                float fraction = consumedCommandTime / command.ElapsedTime;
                Vector3 consumedDisplacement = command.InputModel switch
                {
                    CharacterMotionInputModel.Velocity => command.Value * consumedCommandTime,
                    _ => command.Value * fraction,
                };

                displacement += consumedDisplacement;
                consumedTime += consumedCommandTime;
                remainingStepTime -= consumedCommandTime;
                minimumDistance = MathF.Max(minimumDistance, command.MinDistance);

                float commandTimeRemaining = command.ElapsedTime - consumedCommandTime;
                if (commandTimeRemaining <= DurationEpsilon)
                {
                    _commands[_head] = default;
                    _head = (_head + 1) % _commands.Length;
                    _count--;
                    continue;
                }

                Vector3 valueRemaining = command.InputModel switch
                {
                    CharacterMotionInputModel.Displacement => command.Value - consumedDisplacement,
                    _ => command.Value,
                };
                _commands[_head] = command with
                {
                    Value = valueRemaining,
                    ElapsedTime = commandTimeRemaining,
                };
            }

            if (_count == 0)
                _head = 0;

            return new CharacterMotionStep(
                displacement,
                displacement / fixedDelta,
                minimumDistance,
                consumedTime);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            Array.Clear(_commands);
            _head = 0;
            _count = 0;
        }
    }

    private static CharacterMotionCommand Merge(
        in CharacterMotionCommand first,
        in CharacterMotionCommand second)
    {
        float elapsedTime = first.ElapsedTime + second.ElapsedTime;
        Vector3 displacement = ToDisplacement(first) + ToDisplacement(second);
        return new CharacterMotionCommand(
            displacement,
            CharacterMotionInputModel.Displacement,
            MathF.Max(first.MinDistance, second.MinDistance),
            elapsedTime);
    }

    private static Vector3 ToDisplacement(in CharacterMotionCommand command)
        => command.InputModel == CharacterMotionInputModel.Velocity
            ? command.Value * command.ElapsedTime
            : command.Value;

    private static bool IsFinite(in Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

internal readonly record struct CharacterMotionStep(
    Vector3 Displacement,
    Vector3 Velocity,
    float MinDistance,
    float ConsumedTime);
