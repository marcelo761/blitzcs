Type Element
  Field name$
End Type
Type Owner
  Field elements.Element[1]
End Type
Local owner.Owner=New Owner
Local child.Element=New Element
child\name="held"
owner\elements[0]=child
Delete child
Print 1
Delete owner
For e.Element=Each Element
  Print 99
Next
Print 2
