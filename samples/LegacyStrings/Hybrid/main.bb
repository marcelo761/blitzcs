Include "helpers.bb"
Global shared$=" Door "
<?cs module
public static class StringBridge
{
    public static string Normalize(string value)
    {
        BB.shared = BB.canonical(value);
        return BB.shared;
    }
}
?>
Print CS::StringBridge.Normalize(shared)
Print Upper(Left(shared,1))+Mid(shared,2)
Print RSet(Hex(42),10)
Print Replace("a/b/c","/","\")
Print LSet("x",3)+":"+RSet("x",3)
Print String("=",3)
