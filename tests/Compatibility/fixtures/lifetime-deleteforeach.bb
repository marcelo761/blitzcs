Type Node
    Field value%
End Type

Local node.Node
For i% = 1 To 4
    node = New Node
    node\value = i
Next
For node.Node = Each Node
    Print node\value
    Delete node
Next
Print First Node = Null
Print Last Node = Null
