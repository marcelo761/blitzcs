Type Node
    Field value%
End Type

Local item.Node, alias.Node, tail.Node, previous.Node, following.Node
item = New Node
item\value = 10
alias = item
tail = New Node
tail\value = 30
Delete item
Print item = Null
Print alias = Null
previous = Before alias
following = After alias
Print previous = Null
Print following\value
