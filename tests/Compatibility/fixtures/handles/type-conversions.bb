Type Node
    Field value%
End Type
obj.Node=New Node
h=Handle(obj)
Print Object.Node(1.9)=Null
Print Object.Node("1")=Null
Print Object.Node("junk")=Null
Print Object.Node(0.9)=Null
Print Object.Node(1.9)=obj
Print Object.Node("1")=obj
