# ClassInitGenerator

IronRuby does **not** discover `[RubyMethod]` / `[RubyModule]` / `[RubyClass]` /
`[RubyConstant]` by reflecting at runtime. This tool reflects over the built
library assemblies and emits the registration code that the runtime actually
executes. If you add a new `[RubyMethod]` and do not re-run this generator, the
method is simply never registered.

The project is now SDK-style and targets `net8.0`.

## Build

```bash
export DOTNET_ROOT=/usr/local/dotnet
export PATH=/usr/local/dotnet:$PATH

dotnet build Src/ClassInitGenerator/ClassInitGenerator.csproj
```

Output lands in `Src/ClassInitGenerator/bin/Debug/net8.0/`. Because the project
references `Ruby.csproj`, `IronRuby.Libraries.csproj` and
`IronRuby.Libraries.Yaml.csproj`, that directory also contains freshly built
copies of `IronRuby.dll`, `IronRuby.Libraries.dll` and
`IronRuby.Libraries.Yaml.dll` — those are the assemblies the generator reflects
over, so a plain `dotnet build` of this project is enough to pick up your new
`[RubyMethod]`.

## Regenerate `Src/Libraries/Initializers.Generated.cs`

(The modern replacement for `Src/Libraries/GenerateInitializers.cmd`.)

```bash
D=Src/ClassInitGenerator/bin/Debug/net8.0

dotnet $D/ClassInitGenerator.dll $D/IronRuby.Libraries.dll \
  "/libraries:IronRuby.Builtins;IronRuby.StandardLibrary.Threading;IronRuby.StandardLibrary.Sockets;IronRuby.StandardLibrary.OpenSsl;IronRuby.StandardLibrary.Digest;IronRuby.StandardLibrary.Zlib;IronRuby.StandardLibrary.StringIO;IronRuby.StandardLibrary.StringScanner;IronRuby.StandardLibrary.Enumerator;IronRuby.StandardLibrary.FunctionControl;IronRuby.StandardLibrary.FileControl;IronRuby.StandardLibrary.BigDecimal;IronRuby.StandardLibrary.Iconv;IronRuby.StandardLibrary.ParseTree;IronRuby.StandardLibrary.Open3;IronRuby.StandardLibrary.Win32API" \
  /out:Src/Libraries/Initializers.Generated.cs
```

Note the quotes around `/libraries:` — the list is `;`-separated and the shell
would otherwise split it.

If you add a **new library namespace**, you must add it to that list (and to the
list here in this README), otherwise nothing in it is registered.

## Regenerate `Src/Libraries.Yaml/Initializer.Generated.cs`

```bash
dotnet $D/ClassInitGenerator.dll $D/IronRuby.Libraries.Yaml.dll \
  /libraries:IronRuby.StandardLibrary.Yaml \
  /out:Src/Libraries.Yaml/Initializer.Generated.cs
```

## Regenerate `Src/Ruby/Compiler/ReflectionCache.Generated.cs`

Only needed when you add or change a method/field marked `[Emitted]` in
`Src/Ruby` (`RubyOps` and friends), *not* for `[RubyMethod]` changes.

```bash
dotnet $D/ClassInitGenerator.dll /refcache /out:Src/Ruby/Compiler/ReflectionCache.Generated.cs
```

## Procedure after adding a `[RubyMethod]`

1. Add the method in `Src/Libraries/...`.
2. `dotnet build Src/ClassInitGenerator/ClassInitGenerator.csproj`
3. Run the "Regenerate `Initializers.Generated.cs`" command above.
4. Rebuild the solution and run the tests.

To check what a run *would* change without clobbering the tree, write to a
temporary path and diff (the checked-in files use CRLF):

```bash
dotnet $D/ClassInitGenerator.dll ... /out:/tmp/Initializers.Generated.cs
diff --strip-trailing-cr /tmp/Initializers.Generated.cs Src/Libraries/Initializers.Generated.cs
```

## Known differences vs. the currently checked-in generated files

A regeneration today is *not* byte-identical to what is checked in. All of the
differences were understood and are benign:

* **`#if !SILVERLIGHT` → `#if FEATURE_*`.** The checked-in files are stale: the
  library sources were migrated to `BuildConfig = "FEATURE_FILESYSTEM"` etc.,
  but the initializers were never regenerated (there was no working generator).
  Every `FEATURE_*` symbol used is defined in `Directory.Build.props`, and
  `!SILVERLIGHT` is unconditionally true in this build, so both forms compile
  to the same code.
* **Method ordering.** The generator sorts names with the default
  culture-sensitive `string.CompareTo`. .NET Framework used Windows NLS
  collation; .NET 8 on Linux uses ICU, which orders punctuation differently
  (e.g. `__send__` now sorts before `!`). Registration order between distinct
  names is irrelevant. Beware that this also makes the output mildly
  platform-dependent.
* **Overload ordering.** Overloads are emitted in `Type.GetMethods()` order,
  which is unspecified and differs between .NET Framework and CoreCLR. Four
  method groups are affected (`BigDecimal#<=>`, `IO#readlines`,
  `Kernel#define_singleton_method`, `String#each_line`/`#lines`). The delegate
  list and its parameter-attribute `uint` array permute together, so the
  registered overload set is unchanged.
* **`Microsoft.Scripting.Math.BigInteger` → `System.Numerics.BigInteger`** in
  the YAML initializer: only the mangled loader-method name changes.
* **`ReflectionCache.Generated.cs`:** the generator emits `#if !CLR2` where the
  checked-in file says `#if FEATURE_CORE_DLR` (equivalent here). The checked-in
  file also contains hand-maintained entries that the generator writes
  differently — see the note below.

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
