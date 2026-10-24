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
            var a = serializer.Deserialize(File.ReadAllBytes(path));
            if (a.CacheKey != key) throw new ArtifactFormatException("Cache key mismatch.");
            artifact = a;
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
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
            try { File.Move(tmp, path, true); }
            catch (IOException) when (File.Exists(path)) { File.Delete(tmp); }
        }
        catch { try { File.Delete(tmp); } catch (IOException) { } throw; }
    }

    private string PathFor(string key)
    {
        if (key is null || !KeyPattern().IsMatch(key)) throw new ArgumentException("Cache key must be 8-128 lowercase hex characters.", nameof(key));
        return Path.Combine(Directory, key + ".bca");
    }

    [GeneratedRegex("^[0-9a-f]{8,128}$")]
    private static partial Regex KeyPattern();
}
