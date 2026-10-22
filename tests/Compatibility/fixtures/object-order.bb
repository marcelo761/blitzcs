Type Node
    Field value%
End Type
a.Node=New Node
a\value=1
b.Node=New Node
b\value=2
c.Node=New Node
c\value=3
Insert c Before a
Insert a After b
For p.Node=Each Node
    Print p\value
Next
