using System.Text.RegularExpressions;

namespace BlitzCS.BinaryCompiler;

public sealed partial class TranslationCache(string directory)
{
    private readonly ArtifactSerializer serializer = new();

    public string Directory { get; } = directory;

    public bool TryGet(string key, out TranslationArtifact? artifact)
    {
        var path = PathFor(key);
        artifact = null;
        try
        {
            if (!File.Exists(path)) return false;
            var a = serializer.Deserialize(ReadShared(path));
            if (a.CacheKey != key) throw new ArtifactFormatException("Cache key mismatch.");
            artifact = a;
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException && File.Exists(path)) { return false; }   // still being replaced by a concurrent Put (Windows): a miss, not an error
        catch (ArtifactFormatException)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return false;
        }
    }

    public void Put(string key, TranslationArtifact artifact)
    {
        var path = PathFor(key);
        var bytes = serializer.Serialize(artifact);
        System.IO.Directory.CreateDirectory(Directory);
        var tmp = Path.Combine(Directory, $"{key}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(tmp, bytes);
            Publish(tmp, path);
        }
        catch { try { File.Delete(tmp); } catch (Exception d) when (d is IOException or UnauthorizedAccessException) { } throw; }
    }

    /// <summary>Opened with FileShare.Delete so that a concurrent Put can replace the file while it is being read (Windows refuses to replace a file that is open without it).</summary>
    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>
    /// Atomically replaces the entry. Entries are content-addressed (the key is derived from the input and options), so a concurrent writer publishes identical
    /// bytes. On Windows MoveFileEx can fail transiently with a sharing violation or access denied while another thread or process has the target open or is
    /// replacing it: retry briefly; if the entry exists afterwards, the concurrent writer won and ours is dropped. On Linux rename(2) never fails this way.
    /// </summary>
    private static void Publish(string tmp, string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { File.Move(tmp, path, true); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt < 20) { Thread.Sleep(Math.Min(1 + attempt * 2, 25)); continue; }
                if (File.Exists(path)) { File.Delete(tmp); return; }
                throw;
            }
        }
    }

    private string PathFor(string key)
    {
        if (key is null || !KeyPattern().IsMatch(key)) throw new ArgumentException("Cache key must be 8-128 lowercase hex characters.", nameof(key));
        return Path.Combine(Directory, key + ".bca");
    }

    [GeneratedRegex("^[0-9a-f]{8,128}$")]
    private static partial Regex KeyPattern();
}
