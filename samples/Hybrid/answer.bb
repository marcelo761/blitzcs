; BASIC -> C# -> BASIC, followed by a shared global mutation.
Global answer%=0

<?cs module
public static class MathBridge
{
    public static int Compute() => BB.add(19, 22);
}
?>

answer = CS::MathBridge.Compute()
<?cs
BB.answer += 1;
?>
Print answer
End

Function Add%(left%, right%)
    Return left + right
End Function
