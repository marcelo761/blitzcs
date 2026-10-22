Type Node
    Field value%
    Field link.Node
End Type
a.Node=New Node
a\value=1
b.Node=New Node
b\value=42
alias.Node=a
a\link=b
Delete a
Print a=Null
Print alias=Null
p.Node=After alias
Print p\value
Print First Node=b
Delete Each Node
Print b=Null
