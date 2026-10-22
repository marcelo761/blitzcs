Type Node
    Field value%
    Field successor.Node
End Type

Dim refs.Node(2)
Local a.Node, b.Node, fetched.Node, i%
a = New Node : a\value = 41
b = New Node : b\value = 42
a\successor = b : b\successor = a
refs(0) = a
refs(1) = b
For i = 0 To 1
    fetched = refs(i)
    Print fetched\value
Next
Delete Each Node
Print refs(0) = Null
Print refs(1) = Null
Print First Node = Null
