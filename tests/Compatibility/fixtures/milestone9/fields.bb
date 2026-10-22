Type Child
 Field value%=12
 Field label$="initialized"
End Type
Type Holder
 Field child.Child=New Child
End Type
c.Child=New Child
h.Holder=New Holder
Print c\value
Print "["+c\label+"]"
Print h\child=Null
