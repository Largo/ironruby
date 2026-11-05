# DOOM: an optional test and benchmark

`run.sh` runs the software renderer of [khasinski/doom](https://github.com/khasinski/doom), a
pure-Ruby port of the 1993 engine, drawing E1M1 from four viewpoints (its own
`bench/real_frame_bench.rb`), under CRuby and IronRuby side by side.

    Util/doom/run.sh --fetch           # once: the game at the pinned commit, the shareware WAD
    Util/doom/run.sh                   # Release net10.0, 3 runs each
    Util/doom/run.sh --kernels         # also Util/bench/render.rb
    Util/doom/run.sh --ir-arg -X:NoOSR --ironruby-only

It is a test as well as a benchmark: each viewpoint's framebuffer checksum must be CRuby's, and the
script exits 1 when one is not. It is optional because it needs the network once and is not run by CI.

Nothing is vendored. The game is GPL-2.0 and the WAD is id Software's shareware release, so
`--fetch` puts both under `DOOM_DIR` (default `~/.cache/ironruby-doom`): the game at the commit
pinned in `run.sh`, the WAD checked against the MD5 of shareware DOOM 1.9.

`Util/bench/render.rb` is the renderer's hot loops written out on their own (spans, wall columns,
the per-seg column loop, visplane span assembly), in the same shapes but not the game's code. It
needs nothing fetched, prints a checksum that must match CRuby's, and is what to profile.
