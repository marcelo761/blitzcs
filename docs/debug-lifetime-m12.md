# M12: Debug handle lifetime test failure

## Reproduction

With the original test at baseline `62c1dbb`, run:

```sh
PATH=/var/home/marcelo/.local/share/causoryn-dotnet:$PATH \
  dotnet test tests/BlitzCS.Compiler.Tests/BlitzCS.Compiler.Tests.csproj \
  --configuration Debug \
  --filter FullyQualifiedName~IntegerHandlesDoNotRootDeletedObjectsAndLiveObjectsStillResolve
```

The test fails at `Assert.False(weak.IsAlive)`: after `Collect()`, the deleted
object is still alive. The same focused test passes in Release.

## Diagnosis

The generated runtime removes the handle from `__BlitzHandles.live` when the
object is deleted, clears the object's fields, and leaves only a
`WeakReference` in its position node. The test already removes the generated
static `witness` reference in a no-inline helper before forcing collection.

The remaining root came from the test's earlier `Assert.Single(Table(assembly))`.
`Table` exposes the live handle dictionary as non-generic `IDictionary`, so
`Assert.Single` enumerates boxed `DictionaryEntry` values. That assertion's
enumeration/result keeps the entry's object value alive in Debug through the
subsequent collection. Two isolations establish the source:

* Removing that assertion makes the failing Debug test pass.
* `Assert.Single(Table(assembly).Keys)` also passes; keys contain only integer
  handle IDs and do not retain the target.
* Wrapping the original `Assert.Single(Table(assembly))` in a no-inline helper
  also makes Debug pass. The helper frame has returned before collection.

Together, these experiments show that enumerating the value entry in the test
body leaves the deleted target rooted across the collection boundary in Debug.
The boxed `DictionaryEntry` returned by xUnit's non-generic `Single` is the
likely mechanism; the experiments identify the test assertion's value
enumeration and frame lifetime, without depending on xUnit implementation
details. There is no evidence of a production handle-registry leak: the
registry is empty immediately after deletion, and the collection assertion
passes when the entry value is not enumerated or its assertion frame has ended.

## Correction

Use `Assert.Single(Table(assembly).Keys)` for the pre-deletion cardinality
check. It preserves the one-live-handle condition while enumerating only
integer keys.
Keep the later empty-registry assertion, forced full collection,
`Assert.False(weak.IsAlive)`, handle-number assertion, and `GC.KeepAlive(assembly)`
unchanged. No runtime code change is needed.

Focused Debug verification with this correction passes. The focused Release
test also passes with the original assertion, matching the configuration
difference reported for baseline.
