Type Node
    Field value%
End Type

Local a.Node, b.Node, c.Node, d.Node, e.Node
Local aliasB.Node, aliasC.Node, previous.Node, following.Node, node.Node
a = New Node : a\value = 1
b = New Node : b\value = 2
c = New Node : c\value = 3
d = New Node : d\value = 4
e = New Node : e\value = 5
aliasB = b : aliasC = c
Delete b : Delete c
previous = Before aliasC
following = After aliasB
Print previous\value
Print following\value
Delete a
previous = Before aliasC
Print previous = Null
Delete d
following = After aliasB
Print following\value
For node.Node = Each Node
    Print node\value
Next
