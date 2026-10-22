; C# calls scalar BASIC functions that create and traverse managed legacy objects.
Type Item
    Field value%
End Type

Global total%=0
<?cs module
public static class ObjectsBridge
{
    public static int Populate()
    {
        BB.additem(20);
        BB.additem(22);
        return BB.sumitems();
    }
}
?>

total=CS::ObjectsBridge.Populate()
Print total
Delete Each Item
Print First Item=Null
End

Function AddItem%(n%)
    p.Item=New Item
    p\value=n
    Return n
End Function

Function SumItems%()
    result=0
    For p.Item=Each Item
        result=result+p\value
    Next
    Return result
End Function
