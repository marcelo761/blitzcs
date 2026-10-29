using System.Text;
using Blitz3DCS.Engine;
using Xunit;

namespace Blitz3DCS.Engine.Tests;

public sealed class B3dAnimationPlaybackTests
{
    [Fact]
    public void Loop_and_set_time_use_raw_elapsed_frame_speed_and_positive_modulo()
    {
        var model = Model(10, Keys(1, [0, 0, 0, 0], [10, 10, 0, 0]));
        var playback = new B3dAnimationPlayback(model);

        playback.SetAnimTime(-1);
        Assert.Equal(9, playback.Frame);
        playback.Animate(B3dAnimationMode.Loop, 3);
        playback.Update(7); // 21 frames: no FPS multiplication is applied.
        Assert.Equal(1, playback.Frame);
        playback.Animate(B3dAnimationMode.Loop, -3);
        playback.Update(2);
        Assert.Equal(4, playback.Frame);
    }

    [Fact]
    public void Ping_pong_reflects_at_each_end_and_reverses_playback_speed()
    {
        var playback = new B3dAnimationPlayback(Model(10));
        playback.Animate(B3dAnimationMode.PingPong, 1);

        playback.Update(12);
        Assert.Equal(8, playback.Frame);
        Assert.Equal(-1, playback.Speed);
        playback.Update(2);
        Assert.Equal(6, playback.Frame);
        Assert.Equal(-1, playback.Speed);
    }

    [Fact]
    public void One_shot_stops_at_inclusive_endpoint_without_wrapping_pose_sampling()
    {
        var model = Model(10, Keys(1, [0, 0, 0, 0], [10, 10, 0, 0]));
        var playback = new B3dAnimationPlayback(model);
        var pose = new B3dPose(model);
        playback.Animate(B3dAnimationMode.OneShot, 1);
        playback.Update(10);

        Assert.Equal(10, playback.Frame);
        Assert.False(playback.IsAnimating);
        playback.EvaluateInto(pose);
        Assert.Equal(10, pose.Nodes[0].LocalPosition.X);
    }

    [Fact]
    public void Extracted_sequence_clips_keys_and_preserves_inclusive_range_endpoints()
    {
        var model = Model(20, Keys(1,
            [0, 0, 0, 0], [4, 40, 0, 0], [6, 60, 0, 0], [15, 150, 0, 0], [16, 160, 0, 0]));
        var playback = new B3dAnimationPlayback(model);
        var sequence = playback.AddSequenceRange(5, 15);
        var pose = new B3dPose(model);

        playback.SetAnimTime(0, sequence);
        Assert.Equal(5, playback.Frame);
        playback.EvaluateInto(pose);
        Assert.Equal(60, pose.Nodes[0].LocalPosition.X); // frame 4 lies outside extracted keys.

        playback.Animate(B3dAnimationMode.OneShot, 1, sequence);
        playback.Update(10);
        Assert.Equal(15, playback.Frame);
        playback.EvaluateInto(pose);
        Assert.Equal(150, pose.Nodes[0].LocalPosition.X);
    }

    [Fact]
    public void Transition_progress_uses_elapsed_over_duration_then_advances_the_same_update()
    {
        var playback = new B3dAnimationPlayback(Model(20));
        playback.Animate(B3dAnimationMode.Loop, 2, transitionSeconds: 4);

        Assert.True(playback.IsTransitioning);
        playback.Update(1);
        Assert.Equal(.25f, playback.TransitionProgress, 4);
        Assert.True(playback.IsTransitioning);
        playback.Update(3);

        Assert.False(playback.IsTransitioning);
        Assert.Equal(1, playback.TransitionProgress);
        Assert.Equal(6, playback.Frame); // source advances by speed * full elapsed on the completion update.
    }

    [Fact]
    public void Copy_preserves_sequences_but_resets_playback_and_owns_an_independent_sequence_list()
    {
        var playback = new B3dAnimationPlayback(Model(20));
        var sequence = playback.AddSequenceRange(4, 12);
        playback.Animate(B3dAnimationMode.Loop, 2, sequence, transitionSeconds: 4);
        playback.Update(1);

        var copy = playback.Copy();

        Assert.Equal(playback.SequenceCount, copy.SequenceCount);
        Assert.Equal(0, copy.SequenceIndex);
        Assert.Equal(B3dAnimationMode.Stopped, copy.Mode);
        Assert.Equal(0, copy.Frame);
        Assert.Equal(0, copy.Speed);
        Assert.Equal(0, copy.TransitionProgress);
        Assert.False(copy.IsTransitioning);

        copy.AddSequenceRange(15, 19);
        Assert.Equal(3, copy.SequenceCount);
        Assert.Equal(2, playback.SequenceCount);

        playback.Update(4);
        copy.Update(2);
        Assert.False(playback.IsTransitioning);
        Assert.False(copy.IsAnimating);
        Assert.Equal(4, playback.Frame); // looped to range start after advancing 8 frames.
        Assert.Equal(0, copy.Frame);
    }

    [Fact]
    public void Playback_update_allocates_nothing_after_warmup()
    {
        var playback = new B3dAnimationPlayback(Model(20));
        playback.Animate(B3dAnimationMode.Loop, 1);
        for (var i = 0; i < 32; i++) playback.Update(1);

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 256; i++) playback.Update(1);

        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
    }

    private static B3dModel Model(int frames, params byte[][] chunks) =>
        B3dLoader.Parse(Chunk("BB3D", Join(Int(1), Node("root", Animation(frames), Join(chunks)))));

    private static byte[] Node(string name, params byte[][] chunks) => Chunk("NODE", Join(Build(writer =>
    {
        WriteString(writer, name);
        WriteFloats(writer, 0, 0, 0, 1, 1, 1, 1, 0, 0, 0);
    }), Join(chunks)));

    private static byte[] Animation(int frames) =>
        Chunk("ANIM", Build(writer => { writer.Write(0); writer.Write(frames); writer.Write(24f); }));

    private static byte[] Keys(int flags, params float[][] keys) => Chunk("KEYS", Build(writer =>
    {
        writer.Write(flags);
        foreach (var key in keys)
        {
            writer.Write((int)key[0]);
            for (var i = 1; i < key.Length; i++) writer.Write(key[i]);
        }
    }));

    private static byte[] Build(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true)) write(writer);
        return stream.ToArray();
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        foreach (var character in value) writer.Write((byte)character);
        writer.Write((byte)0);
    }

    private static void WriteFloats(BinaryWriter writer, params float[] values)
    {
        foreach (var value in values) writer.Write(value);
    }

    private static byte[] Int(int value) => Build(writer => writer.Write(value));

    private static byte[] Chunk(string tag, byte[] payload) => Build(writer =>
    {
        writer.Write(Encoding.ASCII.GetBytes(tag));
        writer.Write(payload.Length);
        writer.Write(payload);
    });

    private static byte[] Join(params byte[][] parts)
    {
        using var stream = new MemoryStream();
        foreach (var part in parts) stream.Write(part);
        return stream.ToArray();
    }
}
