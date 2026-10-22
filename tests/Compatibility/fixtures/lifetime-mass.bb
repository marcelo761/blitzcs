Type Node
    Field value%
End Type

Global i%, deleted%
Local node.Node
For i = 1 To 100000
    node = New Node
    node\value = i
    Delete node
    deleted = deleted + 1
Next
Print deleted
Print First Node = Null
