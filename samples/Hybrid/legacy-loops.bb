; BASIC loops -> C# -> BASIC and mutation of a shared global.
Global total%=0

<?cs module
public static class LoopBridge
{
    public static int Value(int n) => BB.twice(n);
}
?>

For i=1 To 3
    If i>0 Then
        total=total+CS::LoopBridge.Value(i)
        <?cs BB.total += 1; ?>
    EndIf
Next
Print total
End

Function Twice%(n%)
    Return n*2
End Function
