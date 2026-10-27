# DLR patches

IronRuby builds against the DLR sources from a sibling `dlr` checkout
(`IronLanguages/dlr`). A few changes this tree needs are not upstream yet, so CI
checks out the upstream revision pinned as `DLR_REF` in `.github/workflows/ci.yml` and
`release.yml` and applies the patches in this directory, in name
order, with `git apply`:

```sh
for p in ironruby/Build/dlr-patches/*.patch; do git -C dlr apply "$p"; done
```

To follow upstream, move `DLR_REF` in both workflows together and check the patches still
apply. Delete a patch once it lands upstream. If one stops applying, CI fails loudly at
the apply step instead of with a confusing CS1061 deep inside IronRuby.

| Patch | Why IronRuby needs it |
|-------|-----------------------|
| `0001-ThreadLocal-cross-thread-read.patch` | `Microsoft.Scripting.Utils.ThreadLocal<T>.TryGetValue(Thread)`, used by `RubyExceptionData` so `Thread#backtrace` can read another thread's interpreted frames. |
| `0002-LightDynamicExpression-materialize-args.patch` | `Reduce()` runs more than once for a node (light interpreter, then background compilation), but the constructor stored the caller's `ReadOnlyCollectionBuilder`, which `ToReadOnlyCollection()` empties on first use. |
| `0003-DlrConfiguration-base-exception.patch` | A language whose constructor throws during startup reported only `InnerException?.Message`, hiding the type and the stack - the only information there is at that point. |
| `0004-InterpretedFrame-handle-exception-frame.patch` | `InterpretedFrame.IsInterpretedFrame` also recognises `Interpreter.HandleException`, where a handler's instructions run: without it an exception raised inside a `rescue` clause had a backtrace missing every frame from the rescuing frame outwards (empty at the top level). |

`aot/` holds patches for the ahead-of-time/NativeAOT prototype (`Util/aot`). CI does not apply
them (the loop above takes this directory's `*.patch` only); `Util/aot/NativeAot/make-dlr-copy.sh`
applies them to a private copy of the DLR.
