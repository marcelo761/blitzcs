Type Node
    Field value%
End Type
Type Other
    Field value%
End Type
nullObj.Node=Null
Print Handle(nullObj)=0
objA.Node=New Node
objA\value=10
hA=Handle(objA)
Print hA
Print Handle(objA)=hA
Print Object.Node(hA)=objA
Print Object.Other(hA)=Null
Print Object.Node(0)=Null
Print Object.Node(-1)=Null
Print Object.Node(9999)=Null
alias.Node=objA
Delete objA
Print Handle(alias)=0
Print Object.Node(hA)=Null
alias=Null
objA=Null
objB.Node=New Node
objB\value=20
hB=Handle(objB)
Print hB
Print hB>hA
Print Object.Node(hA)=Null
Print Object.Node(hB)=objB
Delete objB
Print Handle(objB)=0
Print Object.Node(hB)=Null
