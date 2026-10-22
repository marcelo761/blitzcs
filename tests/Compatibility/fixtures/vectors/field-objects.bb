Type Item
  Field value%
End Type
Type Container
  Field items.Item[2]
End Type
Local holder.Container=New Container
Local child.Item=New Item
Print holder\items[0]=Null
Print holder\items[2]=Null
holder\items[2]=child
holder\items[2]\value=42
Print holder\items[2]\value
Print child\value
Delete holder
Print child\value
Delete child
Delete Each Item
