; reference program 2
Global total# = 0.0
Global label$ = "ref"

Type Item
  Field value%
  Field weight#
  Field name$
End Type

Function Scale%(x%, factor#)
  Local r% = x * factor
  Return r
End Function

Dim squares%(10)
For i = 0 To 9
  squares(i) = i * i
Next

For k = 1 To 3
  it.Item = New Item
  it\value = Scale(k, 2.5)
  it\weight = Float(k) / 4.0
  it\name = label + Str(k)
  total = total + it\weight
Next

For it.Item = Each Item
  Print it\name + ":" + it\value + ":" + it\weight
  Delete it
Next

Print "total=" + total + " sq=" + squares(9)
