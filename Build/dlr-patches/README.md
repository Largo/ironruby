# DLR patches

IronRuby builds against the DLR sources from a sibling `dlr` checkout
(`IronLanguages/dlr`). A few changes this tree needs are not upstream yet, so CI
checks out upstream `main` and applies the patches in this directory, in name
order, with `git apply`:

```sh
for p in ironruby/Build/dlr-patches/*.patch; do git -C dlr apply "$p"; done
```

Delete a patch once it lands upstream. If one stops applying, CI fails loudly at
the apply step instead of with a confusing CS1061 deep inside IronRuby.

| Patch | Why IronRuby needs it |
|-------|-----------------------|
| `0001-ThreadLocal-cross-thread-read.patch` | `Microsoft.Scripting.Utils.ThreadLocal<T>.TryGetValue(Thread)`, used by `RubyExceptionData` so `Thread#backtrace` can read another thread's interpreted frames. |
