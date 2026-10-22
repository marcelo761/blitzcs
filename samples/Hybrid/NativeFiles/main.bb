Include "measure.bb"
<?cs module
public static class NativeFileBridge {
    public static string Prepare() {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "blitzcs-native-" + System.Guid.NewGuid() + ".bin");
        System.IO.File.WriteAllBytes(path, new byte[42]);
        return path;
    }
    public static int Measure(string path) => BB.measure(path);
    public static int Remove(string path) { System.IO.File.Delete(path); return 0; }
}
?>
Local path$=CS::NativeFileBridge.Prepare()
Print CS::NativeFileBridge.Measure(path)
Print FileType(path)
Print measured
CS::NativeFileBridge.Remove(path)
