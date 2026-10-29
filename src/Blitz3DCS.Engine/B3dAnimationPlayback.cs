namespace Blitz3DCS.Engine;

/// <summary>Animation modes supported by the original Blitz3D Animator.</summary>
public enum B3dAnimationMode
{
    Stopped = 0,
    Loop = 1,
    PingPong = 2,
    OneShot = 3
}

/// <summary>An inclusive source key range whose playback length is <c>LastFrame - FirstFrame</c>.</summary>
public readonly record struct B3dAnimationSequence(int FirstFrame, int LastFrame)
{
    public int Length => LastFrame - FirstFrame;
}

/// <summary>
/// CPU-only playback clock for B3D poses. Sequence creation may allocate; updating the clock and
/// evaluating a pose use the caller-owned buffers and do not allocate per frame.
/// </summary>
public sealed class B3dAnimationPlayback
{
    private readonly B3dModel _model;
    private readonly List<B3dAnimationSequence> _sequences = [];
    private int _sequenceIndex;
    private int _mode;
    private float _time;
    private float _speed;
    private float _transitionTime;
    private float _transitionSpeed;
    private bool _active;
    private bool _transitioning;

    public B3dAnimationPlayback(B3dModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _model = model;
        var frameCount = (model.Animation ?? model.FileAnimation)?.FrameCount ?? 0;
        if (frameCount > 0) _sequences.Add(new B3dAnimationSequence(0, frameCount));
    }

    private B3dAnimationPlayback(B3dAnimationPlayback source)
    {
        _model = source._model;
        _sequences.AddRange(source._sequences);
        // Upstream Animator's copy constructor copies the sequence/key data, then reset()s playback.
        _sequenceIndex = 0;
        _mode = 0;
        _time = 0;
        _speed = 0;
        _transitionTime = 0;
        _transitionSpeed = 0;
        _active = false;
        _transitioning = false;
    }

    /// <summary>Copies available sequences into an independent playback stopped at sequence zero.</summary>
    public B3dAnimationPlayback Copy() => new(this);

    public int SequenceCount => _sequences.Count;
    public int SequenceIndex => _sequenceIndex;
    public B3dAnimationMode Mode => (B3dAnimationMode)(_mode & 0x7fff);
    public bool IsAnimating => _mode != 0;
    public bool IsTransitioning => _transitioning;
    public float TransitionProgress => _transitionTime;
    public float SequenceTime => _time;
    public float Speed => _speed;
    public int SequenceLength => _sequences.Count == 0 ? 0 : _sequences[_sequenceIndex].Length;
    public float Frame => _sequences.Count == 0 ? 0 : _sequences[_sequenceIndex].FirstFrame + _time;

    public B3dAnimationSequence GetSequenceRange(int sequence) => GetSequence(sequence);

    /// <summary>Adds an extracted sequence; keys are clipped inclusively to this source range.</summary>
    public int AddSequenceRange(int firstFrame, int lastFrame)
    {
        var length = (long)lastFrame - firstFrame;
        if (length <= 0 || length > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(lastFrame), "An animation sequence must have a positive representable frame length.");
        _sequences.Add(new B3dAnimationSequence(firstFrame, lastFrame));
        return _sequences.Count - 1;
    }

    /// <summary>Sets a static animation time, wrapping it into the selected sequence like SetAnimTime.</summary>
    public void SetAnimTime(float time, int sequence = 0)
    {
        ValidateTime(time, nameof(time));
        var range = GetSequence(sequence);
        _sequenceIndex = sequence;
        _mode = 0;
        _speed = 0;
        _active = false;
        _transitioning = false;
        _transitionTime = 0;
        _time = PositiveModulo(time, range.Length);
    }

    /// <summary>Starts a source-style animation. Speed is frames per elapsed unit, without an FPS factor.</summary>
    public void Animate(B3dAnimationMode mode, float speed, int sequence = 0, float transitionSeconds = 0)
    {
        ValidateTime(speed, nameof(speed));
        ValidateTime(transitionSeconds, nameof(transitionSeconds));
        if (mode is < B3dAnimationMode.Stopped or > B3dAnimationMode.OneShot)
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode == B3dAnimationMode.Stopped && speed == 0)
        {
            _mode = 0;
            _active = false;
            _transitioning = false;
            return;
        }

        var range = GetSequence(sequence);
        _sequenceIndex = sequence;
        _mode = (int)mode;
        _speed = speed;
        _time = speed >= 0 ? 0 : range.Length;
        _transitionTime = 0;

        if (transitionSeconds <= 0)
        {
            _transitioning = false;
            _active = _mode != 0 && _speed != 0;
            if (_speed == 0) _mode = 0;
            return;
        }

        _mode |= 0x8000;
        _transitioning = true;
        _transitionSpeed = 1 / transitionSeconds;
        _active = true;
    }

    /// <summary>Advances playback by elapsed world-update units, matching the native Animator clock.</summary>
    public void Update(float elapsed)
    {
        ValidateTime(elapsed, nameof(elapsed));
        if (!_active) return;

        if (_transitioning)
        {
            _transitionTime += _transitionSpeed * elapsed;
            if (_transitionTime < 1) return;
            _transitioning = false;
            _mode &= 0x7fff;
            if (_mode == 0 || _speed == 0)
            {
                _mode = 0;
                _active = false;
                return;
            }
        }

        var range = _sequences[_sequenceIndex];
        _time += _speed * elapsed;
        switch (Mode)
        {
            case B3dAnimationMode.Loop:
                _time = PositiveModulo(_time, range.Length);
                break;
            case B3dAnimationMode.PingPong:
                _time = PositiveModulo(_time, range.Length * 2f);
                if (_time >= range.Length)
                {
                    _time = range.Length - (_time - range.Length);
                    _speed = -_speed;
                }
                break;
            case B3dAnimationMode.OneShot:
                if (_time < 0)
                {
                    _time = 0;
                    _mode = 0;
                    _active = false;
                }
                else if (_time >= range.Length)
                {
                    _time = range.Length;
                    _mode = 0;
                    _active = false;
                }
                break;
        }
    }

    /// <summary>Evaluates the current absolute frame without global wrapping, clipped to this sequence.</summary>
    public void EvaluateInto(B3dPose pose)
    {
        ArgumentNullException.ThrowIfNull(pose);
        if (!ReferenceEquals(_model, pose.Model)) throw new ArgumentException("The pose belongs to a different B3D model.", nameof(pose));
        if (_sequences.Count == 0) throw new InvalidOperationException("The B3D model has no animation sequence.");
        var range = _sequences[_sequenceIndex];
        B3dAnimationEvaluator.EvaluateInto(_model, Frame, pose, wrapFrames: false,
            rangeStartFrame: range.FirstFrame, rangeEndFrame: range.LastFrame);
    }

    private B3dAnimationSequence GetSequence(int sequence)
    {
        if ((uint)sequence >= (uint)_sequences.Count)
            throw new ArgumentOutOfRangeException(nameof(sequence), "The animation sequence index is invalid.");
        return _sequences[sequence];
    }

    private static float PositiveModulo(float value, float modulus)
    {
        var result = value % modulus;
        if (result < 0) result += modulus;
        return result;
    }

    private static void ValidateTime(float value, string parameterName)
    {
        if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(parameterName, "Animation playback values must be finite.");
    }
}
