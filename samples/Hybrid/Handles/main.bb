Include "objects.bb"
Include "bridge.bb"
shared\value=42
number%=CS::HandleBridge.Number()
Print CS::HandleBridge.Value(number)
CS::HandleBridge.Remove(number)
Print CS::HandleBridge.Value(number)
Print Handle(shared)
