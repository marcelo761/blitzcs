Type Node
    Field value%
End Type

Local node.Node, added.Node
node = New Node : node\value = 1
node = New Node : node\value = 2
For node.Node = Each Node
    Print node\value
    If node\value = 1 Then
        added = New Node
        added\value = 3
    ElseIf node\value = 2 Then
        Delete node
    End If
Next
For node.Node = Each Node
    Print node\value + 10
Next
