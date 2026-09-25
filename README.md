# Fable.SageFs

Run the [Fable](https://github.com/fable-compiler/Fable) compiler live inside a
SageFs session (the live F# REPL daemon, `dotnet tool install -g sagefs`).

> Work in progress. Full documentation comes later.

```sh
dotnet fsi setup.fsx          # download Fable.Compiler, rename its FCS fork into vendor/, generate session/, self-check
dotnet fsi setup.fsx --check  # re-run the self-check only
dotnet fsi setup.fsx --hackable  # also build Fable from source, unoptimized, as hot-patchable SageFs projects
```

Then open a SageFs session on `session/Fable.SageFs.Session.fsproj` (working directory `session/`)
and evaluate `Playground.compileHello ();;`.

Hackable mode (edit or hot-patch Fable itself from the session) is described in [docs/hackable.md](docs/hackable.md).

Pinned versions live in `versions.json`. Nothing under `vendor/`, `.work/`, `session/` or `hackable/` is committed.
