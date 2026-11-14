# ClassInitGenerator

Regenerates `Src/Ruby/Compiler/ReflectionCache.Generated.cs`, the `Methods.X` /
`Fields.X` handles that generated code uses to call `[Emitted]` runtime members.

It used to write the library initializers (`Initializers.Generated.cs`) as well. Those
are now made during the build by the source generator in `Src/InitializerGenerator`;
see its README. A new `[RubyMethod]` needs nothing from this tool.

## Build

```bash
export DOTNET_ROOT=/usr/local/dotnet
export PATH=/usr/local/dotnet:$PATH

dotnet build Src/ClassInitGenerator/ClassInitGenerator.csproj
D=Src/ClassInitGenerator/bin/Debug/net10.0
```

## Regenerate `Src/Ruby/Compiler/ReflectionCache.Generated.cs`

Only needed when you add or change a method/field marked `[Emitted]` in
`Src/Ruby` (`RubyOps` and friends), *not* for `[RubyMethod]` changes.

```bash
dotnet $D/ClassInitGenerator.dll /refcache /out:Src/Ruby/Compiler/ReflectionCache.Generated.cs
```

An `[Emitted]` member that has overloads cannot be cached under its bare name.
Those are declared by hand in the other half of the partial class,
`Src/Ruby/Compiler/ReflectionCache.cs`, which names each overload
(`CreateFrozenMutableStringL` vs `CreateFrozenMutableStringLDebug`) and picks it by
signature; the generator prints a SKIP line for each and carries on. It used to treat
them as an error and exit 1 *without writing the file*, which is why the generated file
had drifted into being hand-maintained. If you add an `[Emitted]` overload and do not
add a hand-written entry, the build fails with a missing `Methods.<name>` - which is
the loud failure you want.

A type's `[Emitted]` members are only picked up if the type itself is
`[ReflectionCached]`. The output is sorted by name, so a hand-inserted entry shows up
as a reordering diff even when its text is right.

The generator emits `#if !CLR2` where the checked-in file says `#if FEATURE_CORE_DLR`
(equivalent here).

### Bug in the checked-in `ReflectionCache.Generated.cs`

The checked-in file has a hand-written `Methods.CreateRegexB` that is a
copy-paste of `CreateRegexL`:

```csharp
public static MethodInfo/*!*/ CreateRegexB { get { return _CreateRegexL ?? (_CreateRegexL = CallInstruction.CacheFunc<System.String, ...>(RubyOps.CreateRegexL)); } }
```

It caches `RubyOps.CreateRegexL(string, ...)` under the name `CreateRegexB`,
even though `RubyOps.CreateRegexB` takes `byte[]`. `Methods.CreateRegexB` is
used by `Src/Ruby/Compiler/Ast/Expressions/RegularExpression.cs`. Regenerating
the file fixes this. Related: the `Yield*`/`YieldSplat*`/`Create*N` entries in
the checked-in file spell `Proc` and `MutableString[]` with short names, which
the generator writes fully qualified — cosmetic, and another sign those lines
were maintained by hand.
