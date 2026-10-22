Type Node
    Field value%
End Type

Local a.Node, b.Node, c.Node, d.Node, node.Node
a = New Node : a\value = 1
b = New Node : b\value = 2
c = New Node : c\value = 3
d = New Node : d\value = 4
Insert d Before b
Insert a After c
For node.Node = Each Node
    Print node\value
Next
