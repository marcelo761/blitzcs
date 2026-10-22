Type Node
    Field value%
End Type

Local node.Node, alias.Node, replacement.Node, firstLive.Node
node = New Node : node\value = 7
alias = node
Delete node
node = Null
replacement = New Node : replacement\value = 9
Print alias = Null
Print replacement\value
firstLive = First Node
Print firstLive\value
