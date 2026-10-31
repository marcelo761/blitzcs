using System.Numerics;
using StbImageSharp;

namespace Blitz3DCS.Engine.Windows;

public static partial class LegacyGraphics
{
    private static readonly Dictionary<int, ImageState> Images = [];
    private static int _nextImageHandle = 1;
    private static bool _autoMidHandle;

    public static int CreateImage(int width, int height, int frames = 1)
    {
        ValidateImageDimensions(width, height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frames);
        CheckThreadIfStarted();

        var texture = CreateTexture(width, height, flags: 2, frames);
        try
        {
            var image = new ImageState(texture, width, height, frames,
                _autoMidHandle ? width / 2 : 0, _autoMidHandle ? height / 2 : 0);
            return RegisterImage(image);
        }
        catch
        {
            FreeTexture(texture);
            throw;
        }
    }

    public static int LoadImage(string file)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        DecodedImage decoded;
        try
        {
            var path = file.Replace('\\', Path.DirectorySeparatorChar);
            using var stream = File.OpenRead(path);
            decoded = DecodeImage(stream);
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
        catch (ArgumentException) { return 0; }
        catch (InvalidOperationException) { return 0; } // StbImageSharp reports unknown/corrupt formats this way.

        // Blitz2D treats black as the default transparent key for DrawImage.
        MaskPixels(decoded.Rgba8, 0, 0, 0);
        return CreateImageFromFrames(decoded.Width, decoded.Height, [decoded.Rgba8]);
    }

    public static int LoadAnimImage(string file, int width, int height, int first, int count)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        ValidateImageDimensions(width, height);
        ArgumentOutOfRangeException.ThrowIfNegative(first);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        DecodedImage decoded;
        try
        {
            var path = file.Replace('\\', Path.DirectorySeparatorChar);
            using var stream = File.OpenRead(path);
            decoded = DecodeImage(stream);
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
        catch (ArgumentException) { return 0; }
        catch (InvalidOperationException) { return 0; }
        var columns = decoded.Width / width;
        var rows = decoded.Height / height;
        if (decoded.Width % width != 0 || decoded.Height % height != 0 || columns == 0 || rows == 0)
            throw new ArgumentException("The source image dimensions must be divisible by the requested frame size.", nameof(file));
        var available = checked(columns * rows);
        if (first > available || count > available - first)
            throw new ArgumentOutOfRangeException(nameof(count), "The requested animation frames are outside the source image.");

        var frames = new byte[count][];
        for (var frame = 0; frame < count; frame++)
        {
            var index = first + frame;
            frames[frame] = CropFrame(decoded.Rgba8, decoded.Width, width, height,
                (index % columns) * width, (index / columns) * height);
            MaskPixels(frames[frame], 0, 0, 0);
        }
        return CreateImageFromFrames(width, height, frames);
    }

    /// <summary>
    /// Original bbFreeImage (bbgraphics.cpp): <c>if( !image_set.erase(i) ) return;</c>. There is no debugImage call
    /// and no error path, so 0, a never-valid handle and an already-freed handle are silent no-ops in every build
    /// (SCP:CB's "Antialiased text" toggle frees a font record's backup image even when that record never had one).
    /// When a frame of the image is the current canvas the original first selects the front canvas, then
    /// <c>delete i</c> frees every frame canvas, and <c>~gxCanvas</c> ends any outstanding lock
    /// (<c>if( locked_cnt ) surf-&gt;Unlock( 0 )</c>) without an error.
    /// </summary>
    public static void FreeImage(int image)
    {
        CheckThreadIfStarted();
        if (!Images.Remove(image, out var state)) return;
        // Only a real free may close the movie: an unmatched FreeImage(0) must not stop a movie whose Image is the default 0.
        if (_movie?.Image == image) BlitzMovie_Close();
        if (!Textures.TryGetValue(state.Texture, out var rep)) return;
        DeselectBuffersOf(rep);
        FreeTexture(state.Texture);
    }

    public static int ImageWidth(int image) => GetImage(image).Width;

    public static int ImageHeight(int image) => GetImage(image).Height;

    public static int ImageBuffer(int image, int frame = 0)
    {
        UpdateMovieImage(image);
        var state = GetImage(image);
        if ((uint)frame >= (uint)state.Frames) throw new ArgumentOutOfRangeException(nameof(frame));
        return TextureBuffer(state.Texture, frame);
    }

    public static void DrawImage(int image, int x, int y, int frame = 0) => DrawImageCore(image, x, y, frame, useAlpha: true, useHandle: true);

    /// <summary>Draws a source rectangle from an image at the requested destination position.</summary>
    public static void DrawImageRect(int image, int x, int y, int sourceX, int sourceY, int sourceWidth, int sourceHeight, int frame = 0)
    {
        UpdateMovieImage(image);
        CheckThreadIfStarted();
        EnsureSelectedBufferUnlocked();
        var state = GetImage(image);
        if ((uint)frame >= (uint)state.Frames) throw new ArgumentOutOfRangeException(nameof(frame));
        var target = GetBuffer(ResolveBufferHandle(0));
        using var binding = AcquireTextureBinding(state.Texture, frame);
        RequireRenderer().DrawTextureRegion(binding.Resource, sourceX, sourceY, sourceWidth, sourceHeight,
            x, y, sourceWidth, sourceHeight, tint: null, alphaBlend: true,
            destination: target.IsBackBuffer ? null : target.Frame!.Resource);
    }

    /// <summary>Tests whether the handle-adjusted image bounds overlap a screen-space rectangle.</summary>
    public static int ImageRectOverlap(int image, int x, int y, int rectangleX, int rectangleY, int rectangleWidth, int rectangleHeight)
    {
        var state = GetImage(image);
        return RectanglesOverlap((long)x - state.HandleX, (long)y - state.HandleY, state.Width, state.Height,
            rectangleX, rectangleY, rectangleWidth, rectangleHeight) ? 1 : 0;
    }

    public static void DrawBlock(int image, int x, int y, int frame = 0) => DrawImageCore(image, x, y, frame, useAlpha: false, useHandle: true);

    public static void MaskImage(int image, int red, int green, int blue)
    {
        ValidateRgb(red, green, blue);
        var state = GetImage(image);
        CheckThreadIfStarted();
        var frames = ReadImageFrames(state);
        foreach (var pixels in frames) MaskPixels(pixels, red, green, blue);
        UploadImageFrames(state, frames);
    }

    public static void HandleImage(int image, int x, int y)
    {
        var state = GetImage(image);
        state.HandleX = x;
        state.HandleY = y;
    }

    public static void MidHandle(int image)
    {
        var state = GetImage(image);
        state.HandleX = state.Width / 2;
        state.HandleY = state.Height / 2;
    }

    public static void AutoMidHandle(int enabled) => _autoMidHandle = enabled != 0;

    public static void ResizeImage(int image, float width, float height)
    {
        var state = GetImage(image);
        if (!float.IsFinite(width) || !float.IsFinite(height) || width == 0 || height == 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be finite and non-zero.");
        var destinationWidth = checked((int)MathF.Abs(width));
        var destinationHeight = checked((int)MathF.Abs(height));
        ValidateImageDimensions(destinationWidth, destinationHeight);

        var frames = ReadImageFrames(state);
        for (var index = 0; index < frames.Length; index++)
            frames[index] = ResizePixels(frames[index], state.Width, state.Height, destinationWidth, destinationHeight,
                flipX: width < 0, flipY: height < 0);
        var handleX = ScaleHandle(state.HandleX, state.Width, destinationWidth, width < 0);
        var handleY = ScaleHandle(state.HandleY, state.Height, destinationHeight, height < 0);
        ReplaceImagePixels(state, frames, destinationWidth, destinationHeight, handleX, handleY);
    }

    public static void ScaleImage(int image, float xScale, float yScale)
    {
        var state = GetImage(image);
        if (!float.IsFinite(xScale) || !float.IsFinite(yScale) || xScale == 0 || yScale == 0)
            throw new ArgumentOutOfRangeException(nameof(xScale), "Image scale factors must be finite and non-zero.");
        ResizeImage(image, state.Width * xScale, state.Height * yScale);
    }

    public static void RotateImage(int image, float angle)
    {
        var state = GetImage(image);
        if (!float.IsFinite(angle)) throw new ArgumentOutOfRangeException(nameof(angle));
        var frames = ReadImageFrames(state);
        var rotated = new ImagePixels[frames.Length];
        for (var index = 0; index < frames.Length; index++)
            rotated[index] = RotatePixels(frames[index], state.Width, state.Height, angle);

        var width = rotated[0].Width;
        var height = rotated[0].Height;
        var handle = RotateHandle(state.HandleX, state.HandleY, state.Width, state.Height, width, height, angle);
        ReplaceImagePixels(state, rotated.Select(static frame => frame.Rgba8).ToArray(), width, height, handle.X, handle.Y);
    }

    internal static DecodedImage DecodeImage(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> signature = stackalloc byte[4];
        var signatureLength = 0;
        while (signatureLength < signature.Length)
        {
            var read = stream.Read(signature[signatureLength..]);
            if (read == 0) break;
            signatureLength += read;
        }
        if (DdsDecoder.HasSignature(signature[..signatureLength]))
        {
            var dds = DdsDecoder.Decode(stream, signature[..signatureLength]);
            return new DecodedImage(dds.Width, dds.Height, dds.Rgba8);
        }

        using var bufferedStream = stream.CanSeek ? null : BufferNonSeekableImage(signature[..signatureLength], stream);
        Stream imageStream;
        if (stream.CanSeek)
        {
            stream.Position -= signatureLength;
            imageStream = stream;
        }
        else
        {
            imageStream = bufferedStream!;
        }
        var decoded = ImageResult.FromStream(imageStream, ColorComponents.RedGreenBlueAlpha);
        ValidateImageDimensions(decoded.Width, decoded.Height);
        if (decoded.Data.Length != checked(decoded.Width * decoded.Height * 4))
            throw new InvalidDataException("The image decoder returned an unexpected pixel buffer length.");
        return new DecodedImage(decoded.Width, decoded.Height, decoded.Data);
    }

    private static MemoryStream BufferNonSeekableImage(ReadOnlySpan<byte> prefix, Stream tail)
    {
        const int maxEncodedBytes = 256 * 1024 * 1024;
        var buffered = new MemoryStream();
        buffered.Write(prefix);
        Span<byte> chunk = stackalloc byte[8192];
        while (true)
        {
            var read = tail.Read(chunk);
            if (read == 0) break;
            if (buffered.Length > maxEncodedBytes - read)
                throw new InvalidDataException("Encoded image exceeds the input size limit.");
            buffered.Write(chunk[..read]);
        }
        buffered.Position = 0;
        return buffered;
    }

    internal static void MaskPixels(byte[] rgba, int red, int green, int blue)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        if (rgba.Length % 4 != 0) throw new ArgumentException("RGBA data must contain complete pixels.", nameof(rgba));
        for (var offset = 0; offset < rgba.Length; offset += 4)
        {
            rgba[offset + 3] = rgba[offset] == red && rgba[offset + 1] == green && rgba[offset + 2] == blue
                ? (byte)0 : (byte)255;
        }
    }

    internal static byte[] ResizePixels(byte[] rgba, int sourceWidth, int sourceHeight, int destinationWidth, int destinationHeight) =>
        ResizePixels(rgba, sourceWidth, sourceHeight, destinationWidth, destinationHeight, flipX: false, flipY: false);

    private static byte[] ResizePixels(byte[] rgba, int sourceWidth, int sourceHeight, int destinationWidth, int destinationHeight, bool flipX, bool flipY)
    {
        ValidatePixelBuffer(rgba, sourceWidth, sourceHeight);
        ValidateImageDimensions(destinationWidth, destinationHeight);
        var resized = new byte[checked(destinationWidth * destinationHeight * 4)];
        for (var y = 0; y < destinationHeight; y++)
        {
            var sourceY = Math.Min(sourceHeight - 1, (int)((long)y * sourceHeight / destinationHeight));
            if (flipY) sourceY = sourceHeight - 1 - sourceY;
            for (var x = 0; x < destinationWidth; x++)
            {
                var sourceX = Math.Min(sourceWidth - 1, (int)((long)x * sourceWidth / destinationWidth));
                if (flipX) sourceX = sourceWidth - 1 - sourceX;
                Buffer.BlockCopy(rgba, (sourceY * sourceWidth + sourceX) * 4,
                    resized, (y * destinationWidth + x) * 4, 4);
            }
        }
        return resized;
    }

    internal static ImagePixels RotatePixels(byte[] rgba, int width, int height, float angle)
    {
        ValidatePixelBuffer(rgba, width, height);
        angle %= 360f;
        var radians = angle * (MathF.PI / 180f);
        var cosine = MathF.Cos(radians);
        var sine = MathF.Sin(radians);
        if (MathF.Abs(cosine) < 1e-6f) cosine = 0;
        if (MathF.Abs(sine) < 1e-6f) sine = 0;
        var outputWidth = Math.Max(1, checked((int)MathF.Ceiling(MathF.Abs(width * cosine) + MathF.Abs(height * sine))));
        var outputHeight = Math.Max(1, checked((int)MathF.Ceiling(MathF.Abs(height * cosine) + MathF.Abs(width * sine))));
        var output = new byte[checked(outputWidth * outputHeight * 4)];
        var sourceCenterX = (width - 1) / 2f;
        var sourceCenterY = (height - 1) / 2f;
        var outputCenterX = (outputWidth - 1) / 2f;
        var outputCenterY = (outputHeight - 1) / 2f;
        for (var y = 0; y < outputHeight; y++)
        for (var x = 0; x < outputWidth; x++)
        {
            // Positive angles rotate clockwise in screen coordinates.
            var dx = x - outputCenterX;
            var dy = y - outputCenterY;
            var sourceX = cosine * dx + sine * dy + sourceCenterX;
            var sourceY = -sine * dx + cosine * dy + sourceCenterY;
            if (sourceX < 0 || sourceY < 0 || sourceX > width - 1 || sourceY > height - 1) continue;
            var x0 = (int)sourceX;
            var y0 = (int)sourceY;
            var x1 = Math.Min(width - 1, x0 + 1);
            var y1 = Math.Min(height - 1, y0 + 1);
            var fx = sourceX - x0;
            var fy = sourceY - y0;
            var p00 = (y0 * width + x0) * 4;
            var p10 = (y0 * width + x1) * 4;
            var p01 = (y1 * width + x0) * 4;
            var p11 = (y1 * width + x1) * 4;
            var destination = (y * outputWidth + x) * 4;
            for (var component = 0; component < 4; component++)
            {
                var top = rgba[p00 + component] + (rgba[p10 + component] - rgba[p00 + component]) * fx;
                var bottom = rgba[p01 + component] + (rgba[p11 + component] - rgba[p01 + component]) * fx;
                output[destination + component] = (byte)Math.Clamp((int)MathF.Round(top + (bottom - top) * fy), 0, 255);
            }
        }
        return new ImagePixels(outputWidth, outputHeight, output);
    }

    private static int CreateImageFromFrames(int width, int height, byte[][] frames)
    {
        CheckThreadIfStarted();
        var texture = CreateTexture(width, height, flags: 2, frames.Length);
        try
        {
            for (var frame = 0; frame < frames.Length; frame++)
                RequireRenderer().UpdateTexturePixels(GetTexture(texture).Frames[frame].Resource, 0, 0, width, height, frames[frame]);
            var image = new ImageState(texture, width, height, frames.Length,
                _autoMidHandle ? width / 2 : 0, _autoMidHandle ? height / 2 : 0);
            return RegisterImage(image);
        }
        catch
        {
            FreeTexture(texture);
            throw;
        }
    }

    private static int RegisterImage(ImageState image)
    {
        var handle = _nextImageHandle;
        var next = checked(handle + 1);
        Images.Add(handle, image);
        _nextImageHandle = next;
        return handle;
    }

    /// <summary>Registers an image over an already-registered texture without a renderer (Linux-runnable lifetime tests).</summary>
    internal static int RegisterImageForTesting(int texture, int width, int height, int frames) =>
        RegisterImage(new ImageState(texture, width, height, frames, 0, 0));

    internal static bool ImageRegisteredForTesting(int image) => Images.ContainsKey(image);

    private static bool RectanglesOverlap(long x1, long y1, long width1, long height1,
        long x2, long y2, long width2, long height2) =>
        !(x1 + width1 <= x2 || x1 >= x2 + width2 || y1 + height1 <= y2 || y1 >= y2 + height2);

    private static void DrawImageCore(int image, int x, int y, int frame, bool useAlpha, bool useHandle)
    {
        var profileStart = EngineProfile.Enabled ? EngineProfile.Now() : 0;
        try { DrawImageCoreImpl(image, x, y, frame, useAlpha, useHandle); }
        finally { if (EngineProfile.Enabled) EngineProfile.Add(EngineProfile.Draw2D, profileStart); }
    }

    private static void DrawImageCoreImpl(int image, int x, int y, int frame, bool useAlpha, bool useHandle)
    {
        UpdateMovieImage(image);
        CheckThreadIfStarted();
        EnsureSelectedBufferUnlocked();
        var state = GetImage(image);
        if ((uint)frame >= (uint)state.Frames) throw new ArgumentOutOfRangeException(nameof(frame));
        var target = GetBuffer(ResolveBufferHandle(0));
        var destination = target.IsBackBuffer ? null : target.Frame!.Resource;
        using var binding = AcquireTextureBinding(state.Texture, frame);
        RequireRenderer().DrawTextureRegion(binding.Resource, 0, 0, state.Width, state.Height,
            x - (useHandle ? state.HandleX : 0), y - (useHandle ? state.HandleY : 0), state.Width, state.Height,
            tint: null, alphaBlend: useAlpha, destination: destination);
    }

    private static ImageState GetImage(int image) => Images.TryGetValue(image, out var state)
        ? state
        : throw new ArgumentOutOfRangeException(nameof(image), $"Unknown image handle {image}.");

    internal static void CloseImages()
    {
        Images.Clear();
        _autoMidHandle = false;
    }

    private static byte[][] ReadImageFrames(ImageState state)
    {
        CheckThreadIfStarted();
        var rep = GetTexture(state.Texture);
        EnsureTextureUnlocked(rep);
        var frames = new byte[state.Frames][];
        for (var index = 0; index < frames.Length; index++)
            frames[index] = RequireRenderer().ReadTexturePixels(rep.Frames[index].Resource);
        return frames;
    }

    private static void UploadImageFrames(ImageState state, byte[][] frames)
    {
        CheckThreadIfStarted();
        if (frames.Length != state.Frames) throw new ArgumentException("Image frame count cannot change during a pixel update.", nameof(frames));
        var rep = GetTexture(state.Texture);
        EnsureTextureUnlocked(rep);
        for (var index = 0; index < frames.Length; index++)
        {
            ValidatePixelBuffer(frames[index], state.Width, state.Height);
            RequireRenderer().UpdateTexturePixels(rep.Frames[index].Resource, 0, 0, state.Width, state.Height, frames[index]);
        }
    }

    private static void ReplaceImagePixels(ImageState state, byte[][] frames, int width, int height, int handleX, int handleY)
    {
        CheckThreadIfStarted();
        EnsureTextureUnlocked(GetTexture(state.Texture));
        if (frames.Length != state.Frames) throw new ArgumentException("Image frame count cannot change during a pixel transform.", nameof(frames));
        var replacement = CreateTexture(width, height, flags: 2, frames.Length);
        try
        {
            var rep = GetTexture(replacement);
            for (var index = 0; index < frames.Length; index++)
            {
                ValidatePixelBuffer(frames[index], width, height);
                RequireRenderer().UpdateTexturePixels(rep.Frames[index].Resource, 0, 0, width, height, frames[index]);
            }
        }
        catch
        {
            FreeTexture(replacement);
            throw;
        }

        var old = state.Texture;
        state.Texture = replacement;
        state.Width = width;
        state.Height = height;
        state.HandleX = handleX;
        state.HandleY = handleY;
        if (Textures.ContainsKey(old)) FreeTexture(old);
    }

    private static byte[] CropFrame(byte[] rgba, int sourceWidth, int frameWidth, int frameHeight, int x, int y)
    {
        var frame = new byte[checked(frameWidth * frameHeight * 4)];
        for (var row = 0; row < frameHeight; row++)
            Buffer.BlockCopy(rgba, ((y + row) * sourceWidth + x) * 4, frame, row * frameWidth * 4, frameWidth * 4);
        return frame;
    }

    private static void ValidateImageDimensions(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        _ = checked(checked(width * height) * 4);
    }

    private static void ValidatePixelBuffer(byte[] rgba, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        ValidateImageDimensions(width, height);
        if (rgba.Length != checked(width * height * 4)) throw new ArgumentException("Pixel buffer dimensions do not match RGBA data.", nameof(rgba));
    }

    private static int ScaleHandle(int handle, int oldSize, int newSize, bool flip)
    {
        var scaled = (int)((long)handle * newSize / oldSize);
        return flip ? newSize - scaled : scaled;
    }

    private static (int X, int Y) RotateHandle(int x, int y, int oldWidth, int oldHeight, int newWidth, int newHeight, float angle)
    {
        angle %= 360f;
        var radians = angle * (MathF.PI / 180f);
        var dx = x - (oldWidth - 1) / 2f;
        var dy = y - (oldHeight - 1) / 2f;
        return (
            (int)MathF.Round(MathF.Cos(radians) * dx - MathF.Sin(radians) * dy + (newWidth - 1) / 2f),
            (int)MathF.Round(MathF.Sin(radians) * dx + MathF.Cos(radians) * dy + (newHeight - 1) / 2f));
    }

    internal readonly record struct DecodedImage(int Width, int Height, byte[] Rgba8);
    internal readonly record struct ImagePixels(int Width, int Height, byte[] Rgba8);

    private sealed class ImageState(int texture, int width, int height, int frames, int handleX, int handleY)
    {
        public int Texture { get; set; } = texture;
        public int Width { get; set; } = width;
        public int Height { get; set; } = height;
        public int Frames { get; } = frames;
        public int HandleX { get; set; } = handleX;
        public int HandleY { get; set; } = handleY;
    }
}
