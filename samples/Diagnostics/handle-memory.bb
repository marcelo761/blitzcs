Type Node
 Field link.Node
End Type
Global lastHandle%, failures%
Function Cycle%(count%)
 For i=1 To count
  n.Node=New Node
  n\link=n
  lastHandle=Handle(n)
  found.Node=Object.Node(lastHandle)
  If found<>n Then failures=failures+1
  Delete n
  found=Object.Node(lastHandle)
  If found<>Null Then failures=failures+1
 Next
 Return 0
End Function
<?cs module
public static class HandleMemoryProbe {
 private static readonly System.Reflection.BindingFlags Flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;
 private static object Table => typeof(BB).Assembly.GetType("__BlitzHandles").GetField("live",Flags).GetValue(null);
 private static object List => typeof(BB).Assembly.GetType("__BlitzObjects").GetField("node",Flags).GetValue(null);
 private static void CollectAndScavenge() {
  System.GC.Collect(System.GC.MaxGeneration,System.GCCollectionMode.Forced,true,true);
  System.GC.WaitForPendingFinalizers();
  var list=List;
  list.GetType().GetMethod("Scavenge",Flags).Invoke(list,null);
  System.GC.Collect(System.GC.MaxGeneration,System.GCCollectionMode.Forced,true,true);
 }
 private static int Positions() {
  var list=List; var node=list.GetType().GetField("head",Flags).GetValue(list); int count=0;
  while(node!=null) { count++; node=node.GetType().GetField("Next",Flags).GetValue(node); }
  return count;
 }
 public static int Run() {
  BB.cycle(1000);
  CollectAndScavenge();
  var rows=new System.Collections.Generic.List<object>();
  for(int batch=1;batch<=2;batch++) {
   var liveBefore=System.GC.GetTotalMemory(false);
   var allocatedBefore=System.GC.GetAllocatedBytesForCurrentThread();
   BB.cycle(100000);
   var allocated=System.GC.GetAllocatedBytesForCurrentThread()-allocatedBefore;
   var beforeCollection=System.GC.GetTotalMemory(false);
   CollectAndScavenge();
   var liveAfter=System.GC.GetTotalMemory(false);
   var info=System.GC.GetGCMemoryInfo();
   var table=Table;
   var entries=((System.Collections.IDictionary)table).Count;
   var capacity=(int)table.GetType().GetMethod("EnsureCapacity").Invoke(table,new object[]{0});
   var positions=Positions();
   rows.Add(new {batch,operations=100000,liveBefore,allocatedBytes=allocated,beforeCollection,liveAfter,
    estimatedReclaimedBytes=System.Math.Max(0,beforeCollection-liveAfter),committedBytes=info.TotalCommittedBytes,
    handleEntries=entries,handleCapacity=capacity,positions,lastHandle=BB.lasthandle,failures=BB.failures});
   if(entries!=0 || positions!=0 || BB.failures!=0) throw new System.InvalidOperationException("Handle stress structural invariant failed.");
  }
  System.Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(rows));
  return 0;
 }
}
?>
CS::HandleMemoryProbe.Run()
