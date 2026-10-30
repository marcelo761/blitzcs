using Blitz3DCS.Graphics.D3D11;
using System.Numerics;

namespace Blitz3DCS.Engine.Windows;

public static partial class LegacyGraphics
{
    private const int BackBufferHandle = 1;
    private const int FirstTextureBufferHandle = -1;
    private const int SupportedTextureFlags = 1 | 2 | 4 | 8 | 16 | 32 | 256;
    private static readonly Dictionary<int, TextureRep> Textures = [];
    private static readonly Dictionary<int, BufferTextureState> TextureBuffers = [];
    private static int _nextTextureHandle = 1;
    private static int _nextTextureBufferHandle = FirstTextureBufferHandle;
    private static int _selectedBuffer = BackBufferHandle;

    internal static void BuildTextureMask(Span<byte> rgba)
    {
        for (var index = 0; index < rgba.Length; index += 4)
            rgba[index + 3] = (rgba[index] | rgba[index + 1] | rgba[index + 2]) == 0 ? (byte)0 : (byte)255;
    }

    public static void ScaleTexture(int texture, float uScale, float vScale)
    {
        CheckThreadIfStarted();
        var rep = GetTexture(texture);
        rep.Scale = new Vector2(1 / uScale, 1 / vScale);
        rep.UpdateTransform();
    }

    public static void PositionTexture(int texture, float uPosition, float vPosition)
    {
        CheckThreadIfStarted();
        var rep = GetTexture(texture);
        rep.Position = new Vector2(-uPosition, -vPosition);
        rep.UpdateTransform();
    }

    public static void RotateTexture(int texture, float angle)
    {
        CheckThreadIfStarted();
        var rep = GetTexture(texture);
        rep.Rotation = -angle * (MathF.PI / 180);
        rep.UpdateTransform();
    }

    public static void TextureCoords(int texture, int coordinateSet)
    {
        CheckThreadIfStarted();
        if ((uint)coordinateSet > 1) throw new ArgumentOutOfRangeException(nameof(coordinateSet));
        GetTexture(texture).CoordinateSet = coordinateSet;
    }

    private static int LoadTextureCore(string file, int flags)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        ValidateTextureFlags(flags);
        CheckThreadIfStarted();
        EnsureNoBufferLocked();
        DecodedImage image;
        try
        {
            using var stream = File.OpenRead(file.Replace('\\', Path.DirectorySeparatorChar));
            image = DecodeImage(stream);
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
        catch (ArgumentException) { return 0; }
        catch (InvalidOperationException) { return 0; }

        if ((flags & 4) != 0) BuildTextureMask(image.Rgba8);
        var texture = CreateTexture(image.Width, image.Height, flags);
        GetTexture(texture).SourceName = file;
        try
        {
            RequireRenderer().UpdateTexturePixels(GetTexture(texture).Frames[0].Resource,
                0, 0, image.Width, image.Height, image.Rgba8);
            return texture;
        }
        catch
        {
            FreeTexture(texture);
            throw;
        }
    }

    /// <summary>Loads a row-major set of fixed-size frames from a texture image.</summary>
    public static int LoadAnimTexture(string file, int flags, int width, int height, int first, int count)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        ValidateTextureFlags(flags);
        CheckThreadIfStarted();
        EnsureNoBufferLocked();

        // Blitz3D treats invalid animation geometry as a request for the whole image.
        if (width <= 0 || height <= 0 || first < 0 || count <= 0)
            return LoadTexture(file, flags);

        DecodedImage image;
        try
        {
            using var stream = File.OpenRead(file.Replace('\\', Path.DirectorySeparatorChar));
            image = DecodeImage(stream);
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
        catch (ArgumentException) { return 0; }
        catch (InvalidOperationException) { return 0; }

        var frames = SliceTextureFrames(image.Rgba8, image.Width, image.Height, width, height, first, count);
        if (frames is null) return 0;
        if ((flags & 4) != 0)
            foreach (var frame in frames) BuildTextureMask(frame);

        var texture = CreateTexture(width, height, flags, frames.Length);
        GetTexture(texture).SourceName = file;
        try
        {
            var renderer = RequireRenderer();
            for (var frame = 0; frame < frames.Length; frame++)
                renderer.UpdateTexturePixels(GetTexture(texture).Frames[frame].Resource,
                    0, 0, width, height, frames[frame]);
            return texture;
        }
        catch
        {
            FreeTexture(texture);
            throw;
        }
    }

    internal static byte[][]? SliceTextureFrames(ReadOnlySpan<byte> rgba, int imageWidth, int imageHeight,
        int frameWidth, int frameHeight, int first, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(imageWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(imageHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameHeight);
        ArgumentOutOfRangeException.ThrowIfNegative(first);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        var expectedBytes = checked(checked(imageWidth * imageHeight) * 4);
        if (rgba.Length != expectedBytes)
            throw new ArgumentException("Source pixel dimensions do not match the RGBA buffer.", nameof(rgba));

        var columns = imageWidth / frameWidth;
        var rows = imageHeight / frameHeight;
        var available = checked(columns * rows);
        if ((long)first + count > available) return null;

        var frameBytes = checked(checked(frameWidth * frameHeight) * 4);
        var frames = new byte[count][];
        for (var frame = 0; frame < count; frame++)
        {
            var index = first + frame;
            var originX = (index % columns) * frameWidth;
            var originY = (index / columns) * frameHeight;
            var pixels = new byte[frameBytes];
            for (var row = 0; row < frameHeight; row++)
            {
                var sourceOffset = ((originY + row) * imageWidth + originX) * 4;
                rgba.Slice(sourceOffset, frameWidth * 4).CopyTo(pixels.AsSpan(row * frameWidth * 4));
            }
            frames[frame] = pixels;
        }
        return frames;
    }

    /// <summary>Creates a procedural texture with one or more frames.</summary>
    private static int CreateTextureCore(int width, int height, int flags, int frames)
    {
        CheckThreadIfStarted();
        ValidateTextureFlags(flags);
        if ((flags & 4) != 0) flags |= 1 | 2;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frames);
        _ = checked(checked(width * height) * 4 * frames);
        EnsureNoBufferLocked();

        var renderer = RequireRenderer();
        var textureHandle = _nextTextureHandle;
        var nextTextureHandle = checked(textureHandle + 1);
        var framesToCreate = new List<TextureFrame>(frames);
        var addedBuffers = new List<int>(frames);
        try
        {
            for (var frameIndex = 0; frameIndex < frames; frameIndex++)
            {
                var gpu = renderer.CreateMutableTexture(width, height, mipmaps: (flags & 8) != 0);
                // Alpha textures begin transparent; opaque textures begin black.
                renderer.ClearTexture(gpu, 0, 0, 0, (flags & 2) != 0 ? 0 : 1);
                var bufferHandle = _nextTextureBufferHandle;
                _nextTextureBufferHandle = checked(bufferHandle - 1);
                var frame = new TextureFrame(gpu, bufferHandle);
                frame.Index = frameIndex;
                framesToCreate.Add(frame);
                addedBuffers.Add(bufferHandle);
            }

            var rep = new TextureRep(textureHandle, width, height, flags, framesToCreate);
            for (var frameIndex = 0; frameIndex < frames; frameIndex++)
            {
                var frame = framesToCreate[frameIndex];
                TextureBuffers.Add(frame.BufferHandle, new BufferTextureState(frame.BufferHandle, rep, frameIndex, frame));
            }
            Textures.Add(textureHandle, rep);
            _nextTextureHandle = nextTextureHandle;
            return textureHandle;
        }
        catch
        {
            foreach (var handle in addedBuffers) TextureBuffers.Remove(handle);
            foreach (var frame in framesToCreate) frame.Resource.Dispose();
            throw;
        }
    }

    public static void FreeTexture(int texture)
    {
        CheckThreadIfStarted();
        if (texture == 0) return;
        EnsureNoBufferLocked();
        var rep = GetTexture(texture);
        Textures.Remove(texture);
        rep.ReleaseCallerHandle();
        DisposeIfUnreferenced(rep);
    }

    /// <summary>Returns the source filename associated with a loaded texture.</summary>
    public static string TextureName(int texture)
    {
        CheckThreadIfStarted();
        return GetTexture(texture).SourceName;
    }

    /// <summary>Returns a caller-owned handle aliasing one of a brush's texture stages.</summary>
    public static int GetBrushTexture(int brush, int index = 0)
    {
        CheckThreadIfStarted();
        if ((uint)index >= 8) throw new ArgumentOutOfRangeException(nameof(index));
        var binding = GetBrush(brush).Textures[index];
        if (binding is null) return 0;

        var rep = binding.Rep;
        ObjectDisposedException.ThrowIf(rep.Disposed, rep);
        var handle = _nextTextureHandle;
        var nextHandle = checked(handle + 1);
        rep.RetainCallerHandle();
        try
        {
            Textures.Add(handle, rep);
            _nextTextureHandle = nextHandle;
            return handle;
        }
        catch
        {
            rep.ReleaseCallerHandle();
            throw;
        }
    }

    public static int TextureWidth(int texture)
    {
        CheckThreadIfStarted();
        return GetTexture(texture).Width;
    }

    public static int TextureHeight(int texture)
    {
        CheckThreadIfStarted();
        return GetTexture(texture).Height;
    }

    /// <summary>Gets the drawing-buffer handle for a zero-based texture frame.</summary>
    public static int TextureBuffer(int texture, int frame = 0)
    {
        CheckThreadIfStarted();
        var rep = GetTexture(texture);
        return rep.Frames[ValidateFrame(rep, frame)].BufferHandle;
    }

    public static void TextureBlend(int texture, int blend)
    {
        CheckThreadIfStarted();
        EnsureNoBufferLocked();
        if (blend is not (0 or 1 or 2 or 3 or 5 or 6))
            throw new NotSupportedException($"Texture blend mode {blend} is not implemented; supported modes are 0, 1, 2, 3, 5, and 6.");
        GetTexture(texture).Blend = blend;
    }

    /// <summary>
    /// Sets one element of the matrix used by blend mode 6 to perturb the next texture stage's UVs.
    /// </summary>
    public static void TextureBumpEnvMat(int texture, int x, int y, float value)
    {
        CheckThreadIfStarted();
        EnsureNoBufferLocked();
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(x, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(y, 1);
        if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "Bump environment values must be finite.");
        GetTexture(texture).BumpEnvMat[x, y] = value;
    }

    /// <summary>Stores the texture's bump-environment luminance scale (consumed only by blend mode 6).</summary>
    public static void TextureBumpEnvScale(int texture, float scale)
    {
        CheckThreadIfStarted();
        EnsureNoBufferLocked();
        if (!float.IsFinite(scale)) throw new ArgumentOutOfRangeException(nameof(scale), "Bump environment scale must be finite.");
        GetTexture(texture).BumpEnvScale = scale;
    }

    /// <summary>Stores the texture's bump-environment luminance offset (consumed only by blend mode 6).</summary>
    public static void TextureBumpEnvOffset(int texture, float offset)
    {
        CheckThreadIfStarted();
        EnsureNoBufferLocked();
        if (!float.IsFinite(offset)) throw new ArgumentOutOfRangeException(nameof(offset), "Bump environment offset must be finite.");
        GetTexture(texture).BumpEnvOffset = offset;
    }

    public static int BackBuffer()
    {
        CheckThreadIfStarted();
        _ = RequireRenderer();
        return BackBufferHandle;
    }

    public static int FrontBuffer() => throw new NotSupportedException(
        "FrontBuffer is not implemented by the flip-model D3D11 renderer; use BackBuffer.");

    /// <summary>Returns the handle currently selected by SetBuffer.</summary>
    public static int GraphicsBuffer()
    {
        CheckThreadIfStarted();
        return _renderer is null ? 0 : _selectedBuffer;
    }

    public static void SetBuffer(int buffer)
    {
        CheckThreadIfStarted();
        EnsureNoBufferLocked();
        _ = RequireRenderer();
        _ = GetBuffer(buffer);
        _selectedBuffer = buffer;
    }

    public static void LockBuffer(int buffer = 0)
    {
        CheckThreadIfStarted();
        _ = RequireRenderer();
        var target = GetBuffer(ResolveBufferHandle(buffer));
        if (target.LockedPixels is not null)
            throw new InvalidOperationException("LockBuffer cannot lock the same buffer more than once.");
        target.LockedPixels = target.IsBackBuffer
            ? RequireRenderer().ReadTexturePixels()
            : RequireRenderer().ReadTexturePixels(target.Frame!.Resource);
    }

    public static void UnlockBuffer(int buffer = 0)
    {
        CheckThreadIfStarted();
        var target = GetBuffer(ResolveBufferHandle(buffer));
        var pixels = target.LockedPixels ?? throw new InvalidOperationException("UnlockBuffer requires a matching LockBuffer call.");
        if (target.IsBackBuffer)
            RequireRenderer().UpdateTexturePixels(null, 0, 0, _width, _height, pixels);
        else
            RequireRenderer().UpdateTexturePixels(target.Frame!.Resource, 0, 0, target.Width, target.Height, pixels);
        target.LockedPixels = null;
    }

    public static void WritePixelFast(int x, int y, int argb, int buffer = 0)
    {
        CheckThreadIfStarted();
        var target = GetBuffer(ResolveBufferHandle(buffer));
        var pixels = target.LockedPixels ?? throw new InvalidOperationException("WritePixelFast requires the target buffer to be locked.");
        var offset = PixelOffset(target, x, y);
        pixels[offset] = (byte)(argb >> 16);
        pixels[offset + 1] = (byte)(argb >> 8);
        pixels[offset + 2] = (byte)argb;
        pixels[offset + 3] = (byte)((uint)argb >> 24);
    }

    public static int ReadPixelFast(int x, int y, int buffer = 0)
    {
        CheckThreadIfStarted();
        var target = GetBuffer(ResolveBufferHandle(buffer));
        var pixels = target.LockedPixels ?? throw new InvalidOperationException("ReadPixelFast requires the target buffer to be locked.");
        var offset = PixelOffset(target, x, y);
        return unchecked((int)((uint)pixels[offset + 3] << 24 | (uint)pixels[offset] << 16 |
            (uint)pixels[offset + 1] << 8 | pixels[offset + 2]));
    }

    /// <summary>Copies a clipped rectangle between the selected buffer and explicit texture buffers.</summary>
    public static void CopyRect(int sourceX, int sourceY, int width, int height, int destinationX, int destinationY,
        int sourceBuffer = 0, int destinationBuffer = 0)
    {
        var profileStart = EngineProfile.Enabled ? EngineProfile.Now() : 0;
        try { CopyRectImpl(sourceX, sourceY, width, height, destinationX, destinationY, sourceBuffer, destinationBuffer); }
        finally { if (EngineProfile.Enabled) EngineProfile.Add(EngineProfile.Copy, profileStart); }
    }

    private static void CopyRectImpl(int sourceX, int sourceY, int width, int height, int destinationX, int destinationY,
        int sourceBuffer, int destinationBuffer)
    {
        CheckThreadIfStarted();
        var renderer = RequireRenderer();
        var source = GetBuffer(ResolveBufferHandle(sourceBuffer));
        var destination = GetBuffer(ResolveBufferHandle(destinationBuffer));
        var clipped = ClipCopy(sourceX, sourceY, width, height, destinationX, destinationY,
            source.Width, source.Height, destination.Width, destination.Height);
        if (clipped.Width == 0 || clipped.Height == 0) return;

        if (source.LockedPixels is null && destination.LockedPixels is null)
        {
            renderer.CopyTextureRegion(source.IsBackBuffer ? null : source.Frame!.Resource,
                destination.IsBackBuffer ? null : destination.Frame!.Resource,
                clipped.SourceX, clipped.SourceY, clipped.DestinationX, clipped.DestinationY, clipped.Width, clipped.Height);
            return;
        }

        var sourcePixels = source.LockedPixels ?? (source.IsBackBuffer
            ? renderer.ReadTexturePixels()
            : renderer.ReadTexturePixels(source.Frame!.Resource));
        var copiedPixels = CopyPixels(sourcePixels, source.Width, clipped);
        if (destination.LockedPixels is { } destinationPixels)
            PastePixels(copiedPixels, destinationPixels, destination.Width, clipped);
        else
            renderer.UpdateTexturePixels(destination.IsBackBuffer ? null : destination.Frame!.Resource,
                clipped.DestinationX, clipped.DestinationY, clipped.Width, clipped.Height, copiedPixels);
    }

    /// <summary>Clears the selected texture buffer or the D3D11 backbuffer.</summary>
    internal static void ClearSelectedBuffer(float red, float green, float blue)
    {
        CheckThreadIfStarted();
        EnsureNoBufferLocked();
        var target = GetBuffer(_selectedBuffer);
        if (target.IsBackBuffer)
            RequireRenderer().Clear(red, green, blue);
        else
            RequireRenderer().ClearTexture(target.Frame!.Resource, red, green, blue, 1);
    }

    /// <summary>Acquires an immutable material view of a texture frame.</summary>
    internal static TextureBinding AcquireTextureBinding(int texture, int frame = 0)
    {
        CheckThreadIfStarted();
        var rep = GetTexture(texture);
        var selectedFrame = rep.Frames[ValidateFrame(rep, frame)];
        checked { rep.BindingReferences++; }
        return new TextureBinding(rep, selectedFrame);
    }

    internal static void ReleaseTextureBinding(TextureBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        binding.Dispose();
    }

    /// <summary>Releases all caller-owned textures before the renderer itself is disposed.</summary>
    internal static void CloseTextureBuffers()
    {
        CheckThreadIfStarted();
        foreach (var rep in Textures.Values.ToArray())
        {
            rep.CallerHandles = 0;
            DisposeIfUnreferenced(rep);
        }
        Textures.Clear();
        BackBufferState.Instance.LockedPixels = null;
        _selectedBuffer = BackBufferHandle;
        // Any outstanding binding has already been released by the graphics material owner.
        foreach (var rep in TextureBuffers.Values.Select(buffer => buffer.Rep).Distinct().ToArray())
        {
            foreach (var frame in rep.Frames) frame.Resource.Dispose();
            rep.Disposed = true;
        }
        TextureBuffers.Clear();
        _nextTextureHandle = 1;
        _nextTextureBufferHandle = FirstTextureBufferHandle;
    }

    internal static void EnsureNoBufferLocked()
    {
        if (BackBufferState.Instance.LockedPixels is not null || TextureBuffers.Values.Any(buffer => buffer.LockedPixels is not null))
            throw new InvalidOperationException("Only pixel read/write/copy operations are allowed while a buffer is locked.");
    }

    internal static void ValidateTextureFlags(int flags)
    {
        if (flags < 0 || (flags & ~SupportedTextureFlags) != 0)
            throw new NotSupportedException($"Texture flags 0x{flags:X} include unsupported options; implemented flags are color (1), alpha (2), black mask (4), mipmaps (8), clamp U (16), clamp V (32), and store in video memory (256).");
    }

    internal static byte[] ArgbToRgbaPixels(ReadOnlySpan<int> argb)
    {
        var rgba = new byte[checked(argb.Length * 4)];
        for (var i = 0; i < argb.Length; i++)
        {
            var pixel = unchecked((uint)argb[i]);
            var offset = i * 4;
            rgba[offset] = (byte)(pixel >> 16);
            rgba[offset + 1] = (byte)(pixel >> 8);
            rgba[offset + 2] = (byte)pixel;
            rgba[offset + 3] = (byte)(pixel >> 24);
        }
        return rgba;
    }

    internal static int RgbaToArgb(byte red, byte green, byte blue, byte alpha) =>
        unchecked((int)((uint)alpha << 24 | (uint)red << 16 | (uint)green << 8 | blue));

    internal static CopyRegion ClipCopy(int sourceX, int sourceY, int width, int height,
        int destinationX, int destinationY, int sourceWidth, int sourceHeight, int destinationWidth, int destinationHeight)
    {
        if (width < 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height < 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (width == 0 || height == 0) return default;

        long sx = sourceX, sy = sourceY, dx = destinationX, dy = destinationY, w = width, h = height;
        if (sx < 0) { var delta = -sx; sx = 0; dx += delta; w -= delta; }
        if (sy < 0) { var delta = -sy; sy = 0; dy += delta; h -= delta; }
        if (dx < 0) { var delta = -dx; dx = 0; sx += delta; w -= delta; }
        if (dy < 0) { var delta = -dy; dy = 0; sy += delta; h -= delta; }
        w = Math.Min(w, sourceWidth - sx);
        h = Math.Min(h, sourceHeight - sy);
        w = Math.Min(w, destinationWidth - dx);
        h = Math.Min(h, destinationHeight - dy);
        if (w <= 0 || h <= 0) return default;
        return new CopyRegion(checked((int)sx), checked((int)sy), checked((int)dx), checked((int)dy), checked((int)w), checked((int)h));
    }

    internal static void RegisterTextureForTesting(TextureRep rep) => Textures.Add(rep.Handle, rep);
    internal static void ForgetTextureForTesting(int handle) => Textures.Remove(handle);

    private static TextureRep GetTexture(int texture) => Textures.TryGetValue(texture, out var rep)
        ? rep : throw new ArgumentException($"Texture handle {texture} is invalid or has been freed.", nameof(texture));

    private static BufferState GetBuffer(int buffer)
    {
        if (buffer == BackBufferHandle)
        {
            _ = RequireRenderer();
            return BackBufferState.Instance;
        }
        if (TextureBuffers.TryGetValue(buffer, out var state) && !state.Rep.Disposed) return state;
        throw new ArgumentException($"Buffer handle {buffer} is invalid or has been freed.", nameof(buffer));
    }

    private static int ResolveBufferHandle(int buffer) => buffer == 0 ? _selectedBuffer : buffer;

    private static int ValidateFrame(TextureRep rep, int frame)
    {
        if ((uint)frame >= (uint)rep.Frames.Count)
            throw new ArgumentOutOfRangeException(nameof(frame), frame, $"Texture frame must be from 0 through {rep.Frames.Count - 1}.");
        return frame;
    }

    private static int PixelOffset(BufferState buffer, int x, int y)
    {
        if ((uint)x >= (uint)buffer.Width) throw new ArgumentOutOfRangeException(nameof(x));
        if ((uint)y >= (uint)buffer.Height) throw new ArgumentOutOfRangeException(nameof(y));
        return checked((y * buffer.Width + x) * 4);
    }

    private static byte[] CopyPixels(byte[] source, int sourceWidth, CopyRegion region)
    {
        var result = new byte[checked(region.Width * region.Height * 4)];
        var rowBytes = checked(region.Width * 4);
        for (var row = 0; row < region.Height; row++)
            Buffer.BlockCopy(source, checked(((region.SourceY + row) * sourceWidth + region.SourceX) * 4),
                result, row * rowBytes, rowBytes);
        return result;
    }

    private static void PastePixels(byte[] source, byte[] destination, int destinationWidth, CopyRegion region)
    {
        var rowBytes = checked(region.Width * 4);
        for (var row = 0; row < region.Height; row++)
            Buffer.BlockCopy(source, row * rowBytes, destination,
                checked(((region.DestinationY + row) * destinationWidth + region.DestinationX) * 4), rowBytes);
    }

    private static void DisposeIfUnreferenced(TextureRep rep)
    {
        if (rep.CallerHandles != 0 || rep.BindingReferences != 0 || rep.Disposed) return;
        if (rep.Frames.Any(frame => frame.BufferHandle == _selectedBuffer)) _selectedBuffer = BackBufferHandle;
        foreach (var frame in rep.Frames)
        {
            TextureBuffers.Remove(frame.BufferHandle);
            frame.Resource.Dispose();
        }
        rep.Disposed = true;
    }

    private static void ReleaseBinding(TextureRep rep)
    {
        if (rep.Disposed) return;
        if (rep.BindingReferences <= 0) throw new InvalidOperationException("Texture binding reference count is corrupt.");
        rep.BindingReferences--;
        DisposeIfUnreferenced(rep);
    }

    internal readonly record struct CopyRegion(int SourceX, int SourceY, int DestinationX, int DestinationY, int Width, int Height);

    internal sealed class TextureBinding : IDisposable
    {
        private readonly TextureRep _rep;
        private readonly TextureFrame _frame;
        private bool _disposed;

        internal TextureBinding(TextureRep rep, TextureFrame frame)
        {
            _rep = rep;
            _frame = frame;
        }

        internal D3D11Texture Resource => _frame.Resource;
        internal TextureRep Rep => _rep;
        internal int Frame => _frame.Index;
        internal int Blend => _rep.Blend;
        internal bool UseAlpha => (_rep.Flags & 2) != 0;
        internal bool Masked => (_rep.Flags & 4) != 0;
        internal bool ClampU => (_rep.Flags & 16) != 0;
        internal bool ClampV => (_rep.Flags & 32) != 0;
        internal Matrix3x2 UvTransform => _rep.UvTransform;
        internal int CoordinateSet => _rep.CoordinateSet;
        internal Vector4 BumpMatrix => new(_rep.BumpEnvMat[0, 0], _rep.BumpEnvMat[0, 1],
            _rep.BumpEnvMat[1, 0], _rep.BumpEnvMat[1, 1]);
        internal float BumpScale => _rep.BumpEnvScale;
        internal float BumpOffset => _rep.BumpEnvOffset;

        internal TextureBinding Retain()
        {
            ObjectDisposedException.ThrowIf(_disposed || _rep.Disposed, this);
            checked { _rep.BindingReferences++; }
            return new TextureBinding(_rep, _frame);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseBinding(_rep);
        }
    }

    private abstract class BufferState
    {
        public abstract bool IsBackBuffer { get; }
        public abstract int Width { get; }
        public abstract int Height { get; }
        public abstract TextureFrame? Frame { get; }
        public byte[]? LockedPixels { get; set; }
    }

    private sealed class BackBufferState : BufferState
    {
        public static BackBufferState Instance { get; } = new();
        public override bool IsBackBuffer => true;
        public override int Width => _width;
        public override int Height => _height;
        public override TextureFrame? Frame => null;
    }

    private sealed class BufferTextureState(int bufferHandle, TextureRep rep, int frameIndex, TextureFrame frame) : BufferState
    {
        public int BufferHandle { get; } = bufferHandle;
        public TextureRep Rep { get; } = rep;
        public int FrameIndex { get; } = frameIndex;
        public override bool IsBackBuffer => false;
        public override int Width => Rep.Width;
        public override int Height => Rep.Height;
        public override TextureFrame? Frame => frame;
    }

    internal sealed class TextureRep(int handle, int width, int height, int flags, List<TextureFrame> frames)
    {
        public int Handle { get; } = handle;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public int Flags { get; } = flags;
        public string SourceName { get; set; } = string.Empty;
        public List<TextureFrame> Frames { get; } = frames;
        public int Blend { get; set; } = 2;
        public float[,] BumpEnvMat { get; } = new float[2, 2];
        public float BumpEnvScale { get; set; }
        public float BumpEnvOffset { get; set; }
        public Vector2 Scale { get; set; } = Vector2.One;
        public Vector2 Position { get; set; }
        public float Rotation { get; set; }
        public int CoordinateSet { get; set; }
        public Matrix3x2 UvTransform { get; private set; } = Matrix3x2.Identity;
        public void UpdateTransform()
        {
            var c = MathF.Cos(Rotation);
            var s = MathF.Sin(Rotation);
            UvTransform = new Matrix3x2(c * Scale.X, -s * Scale.Y,
                s * Scale.X, c * Scale.Y, Position.X, Position.Y);
        }
        public int BindingReferences { get; set; }
        public int CallerHandles { get; set; } = 1;
        public bool Disposed { get; set; }
        internal void RetainCallerHandle()
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            checked { CallerHandles++; }
        }
        internal void ReleaseCallerHandle()
        {
            if (CallerHandles <= 0) throw new InvalidOperationException("Texture handle reference count is corrupt.");
            CallerHandles--;
        }
    }

    internal sealed class TextureFrame(D3D11Texture resource, int bufferHandle)
    {
        public D3D11Texture Resource { get; } = resource;
        public int BufferHandle { get; } = bufferHandle;
        public int Index { get; set; }
    }
}
