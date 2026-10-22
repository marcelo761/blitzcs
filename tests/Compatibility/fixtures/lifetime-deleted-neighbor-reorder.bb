Type Node
    Field value%
End Type

Local a.Node, b.Node, c.Node, aliasB.Node, neighbor.Node
a = New Node : a\value = 1
b = New Node : b\value = 2
c = New Node : c\value = 3
aliasB = b
Delete b
Insert c Before a
neighbor = After aliasB
Print neighbor = Null
neighbor = Before aliasB
Print neighbor\value
Insert c After a
neighbor = After aliasB
Print neighbor = Null
If neighbor <> Null Then Print neighbor\value
neighbor = Before aliasB
If neighbor <> Null Then Print neighbor\value
