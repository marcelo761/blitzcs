Type Node
    Field value%
End Type
a.Node=New Node
a\value=1
b.Node=New Node
b\value=2
c.Node=New Node
c\value=3
For p.Node=Each Node
    Print p\value
    Delete p
Next
Print First Node=Null
