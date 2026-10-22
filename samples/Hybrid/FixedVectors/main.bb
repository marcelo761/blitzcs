Include "state.bb"
Include "bridge.bb"

values%[2]=10
result%=CS::FixedVectorBridge.AddThroughBasic(32)
Print result%

ratio#=CS::FixedVectorBridge.SetRatio(3.5)
Print ratio#
