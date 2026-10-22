<?cs module
public static class PropertyProbe
{
    public static int HasTrailingSeparator(string value) => value.EndsWith("\\") ? 1 : 0;
}
?>
Print "unknown-empty="+(SystemProperty("m16_unknown_property")="")
Print "cpu-case-insensitive="+(SystemProperty("CPU")=SystemProperty("cpu"))
Print "tempdir-nonempty="+(SystemProperty("tempdir")<>"")
Print "tempdir-trailing-separator="+CS::PropertyProbe.HasTrailingSeparator(SystemProperty("TEMPDIR"))
