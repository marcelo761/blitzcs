Global total%=0
Function SumNative%(left%,right%)
    total=NativeAdd(left,right)
    Return total
End Function
<?cs module
public static class NativeHybrid {
    public static int Sum(int left,int right) => BB.sumnative(left,right);
}
?>
Print CS::NativeHybrid.Sum(20,22)
Print NativeScale(1.75,2.0)
Print NativeUtf8Length("Olá 🌱")
Print NativeMessage$("Olá 🌱")
Print NativeByte(255)
Print total
Print NativeClose()
