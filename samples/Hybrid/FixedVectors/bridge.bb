<?cs module
public static class FixedVectorBridge
{
    public static int AddThroughBasic(int value) => BB.addtovector(value);
    public static float SetRatio(float value) => BB.updateratio(value);
}
?>
