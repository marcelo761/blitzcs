Type Node
    Field value%
End Type
a.Node=New Node
a\value=10
b.Node=New Node
b\value=20
c.Node=New Node
c\value=30
ha=Handle(a)
hb=Handle(b)
hc=Handle(c)
alias.Node=b
Delete b
Print Handle(alias)=0
Print Object.Node(hb)=Null
p.Node=Before alias
q.Node=After alias
Print p\value
Print q\value
Print Handle(p)=ha
Print Handle(q)=hc
