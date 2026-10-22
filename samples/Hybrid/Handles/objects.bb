Type Node
 Field value%
End Type
Global shared.Node=New Node
Function Number%()
 Return Handle(shared)
End Function
Function Value%(number%)
 n.Node=Object.Node(number)
 If n=Null Then Return -1
 Return n\value
End Function
Function Remove%(number%)
 n.Node=Object.Node(number)
 Delete n
 Return 0
End Function
