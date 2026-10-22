Type Node
    Field value%
    Field successor.Node
    Field prior.Node
End Type

Local a.Node, b.Node, c.Node, alias.Node
a = New Node : a\value = 11
b = New Node : b\value = 22
c = New Node : c\value = 33
a\successor = b : b\successor = c : c\successor = a
a\prior = c : b\prior = a : c\prior = b
alias = b
Delete a
Delete b
Print a = Null
Print alias = Null
Print c\successor = Null
Print c\prior = Null
Delete c
Print First Node = Null
