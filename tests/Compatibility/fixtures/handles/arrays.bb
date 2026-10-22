Type Node
    Field value%
End Type
Dim handles%(1)
Dim nodes.Node(1)
a.Node=New Node
b.Node=New Node
a\value=10
b\value=20
handles(0)=Handle(a)
handles(1)=Handle(b)
nodes(0)=a
nodes(1)=b
Print Object.Node(handles(0))=nodes(0)
Print Object.Node(handles(1))=nodes(1)
Delete a
Print Object.Node(handles(0))=Null
Print Object.Node(handles(1))=nodes(1)
Delete b
Print Object.Node(handles(1))=Null
