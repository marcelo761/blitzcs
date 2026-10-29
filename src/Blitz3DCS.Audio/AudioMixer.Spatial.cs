using System.Numerics;

namespace Blitz3DCS.Audio;

public readonly record struct AudioListenerState(Vector3 Position, Vector3 Velocity, Vector3 Forward, Vector3 Up)
{
    public static AudioListenerState Default => new(Vector3.Zero, Vector3.Zero, Vector3.UnitZ, Vector3.UnitY);
}

public readonly record struct AudioEmitterState(Vector3 Position, Vector3 Velocity, float MinimumDistance = 1f, float MaximumDistance = 10000f);

public readonly record struct AudioSpatialSettings(float RolloffFactor, float DopplerFactor, float DistanceFactor, float SpeedOfSound)
{
    public static AudioSpatialSettings Default => new(1, 1, 1, 343.3f);
}

public sealed partial class AudioMixer
{
    private AudioListenerState _listener = AudioListenerState.Default;
    private AudioSpatialSettings _spatialSettings = AudioSpatialSettings.Default;
    private Vector3 _listenerRight = Vector3.UnitX;

    /// <summary>Sets listener orientation in a left-handed world; velocities use world units per second.</summary>
    public void SetListener(AudioListenerState listener)
    {
        ValidateVector(listener.Position, nameof(listener));
        ValidateVector(listener.Velocity, nameof(listener));
        ValidateVector(listener.Forward, nameof(listener));
        ValidateVector(listener.Up, nameof(listener));
        var right = Vector3.Cross(listener.Up, listener.Forward);
        if (!float.IsFinite(right.LengthSquared()) || right.LengthSquared() < 1e-12f)
            throw new ArgumentException("Listener forward and up must be nonzero and nonparallel.", nameof(listener));
        _listenerRight = Vector3.Normalize(right);
        _listener = listener;
    }

    public void SetSpatialSettings(AudioSpatialSettings settings)
    {
        if (!float.IsFinite(settings.RolloffFactor) || settings.RolloffFactor < 0
            || !float.IsFinite(settings.DopplerFactor) || settings.DopplerFactor < 0
            || !float.IsFinite(settings.DistanceFactor) || settings.DistanceFactor <= 0
            || !float.IsFinite(settings.SpeedOfSound) || settings.SpeedOfSound <= 0
            || !float.IsFinite(settings.SpeedOfSound / settings.DistanceFactor))
            throw new ArgumentOutOfRangeException(nameof(settings), "Spatial factors must be finite; distance factor and sound speed must be positive.");
        _spatialSettings = settings;
    }

    public void SetChannel3D(int channel, AudioEmitterState emitter)
    {
        ValidateVector(emitter.Position, nameof(emitter));
        ValidateVector(emitter.Velocity, nameof(emitter));
        if (!float.IsFinite(emitter.MinimumDistance) || emitter.MinimumDistance <= 0
            || !float.IsFinite(emitter.MaximumDistance) || emitter.MaximumDistance < emitter.MinimumDistance)
            throw new ArgumentOutOfRangeException(nameof(emitter), "Source distances must be finite, positive and ordered.");
        GetChannel(channel).Emitter = emitter;
    }

    public void SetChannel2D(int channel) => GetChannel(channel).Emitter = null;

    private (float Left, float Right, double Pitch) CalculateSpatial(AudioEmitterState emitter)
    {
        var delta = emitter.Position - _listener.Position;
        var distance = delta.Length();
        if (!float.IsFinite(distance))
            throw new ArgumentOutOfRangeException(nameof(emitter), "Listener/source separation exceeds the spatial model's finite range.");
        var direction = distance > 1e-6f ? delta / distance : Vector3.Zero;
        var pan = Math.Clamp(Vector3.Dot(direction, _listenerRight), -1, 1);
        var clampedDistance = Math.Clamp(distance, emitter.MinimumDistance, emitter.MaximumDistance);
        // Inverse-distance clamped model (OpenAL 1.1, section 3.4.2); evaluated once per channel block.
        var attenuation = emitter.MinimumDistance / (emitter.MinimumDistance
            + _spatialSettings.RolloffFactor * (clampedDistance - emitter.MinimumDistance));
        var pitch = 1.0;
        if (_spatialSettings.DopplerFactor > 0 && distance > 1e-6f)
        {
            var speed = (double)_spatialSettings.SpeedOfSound / _spatialSettings.DistanceFactor;
            var radialLimit = speed * (1 - 1e-6);
            var listenerVelocity = Math.Clamp((double)Vector3.Dot(_listener.Velocity, direction) * _spatialSettings.DopplerFactor, -radialLimit, radialLimit);
            var sourceVelocity = Math.Clamp((double)Vector3.Dot(emitter.Velocity, direction) * _spatialSettings.DopplerFactor, -radialLimit, radialLimit);
            pitch = (speed + listenerVelocity) / (speed + sourceVelocity);
        }
        return (attenuation * MathF.Sqrt((1 - pan) * 0.5f), attenuation * MathF.Sqrt((1 + pan) * 0.5f), pitch);
    }

    private static void ValidateVector(Vector3 value, string parameter)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            throw new ArgumentOutOfRangeException(parameter, "Spatial coordinates and velocities must be finite.");
    }
}
