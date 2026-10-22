; Integer, Float and String round-trip through BASIC and C# in one assembly.
Global number%=20
Global fraction#=1.5
Global message$="Blitz"

<?cs module
public static class TypedBridge
{
    public static int Number(int value) => BB.plus(value, 2);
    public static float Fraction(float value) => BB.scale(value, 2.0f);
    public static string Message(string value) => BB.decorate(value);
}
?>

number=CS::TypedBridge.Number(number)
fraction=CS::TypedBridge.Fraction(fraction)
message=CS::TypedBridge.Message(message)

<?cs
BB.number += 20;
BB.fraction += 0.5f;
BB.message += " C#";
?>

Print number
Print fraction
Print message
End

Function Plus%(value%, delta%)
    Return value+delta
End Function

Function Scale#(value#, factor#)
    Return value*factor
End Function

Function Decorate$(value$)
    Return "["+value+"]"
End Function
