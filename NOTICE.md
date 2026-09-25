# Third-party software

This repository contains only scripts, templates, a sample and documentation. It does not contain or
redistribute any third-party binaries.

When you run `setup.fsx`, it does the following on your machine:

- It downloads these NuGet packages from nuget.org, or reuses them from your NuGet cache:
  - `Fable.Compiler` and `Fable.AST`, from the [Fable](https://github.com/fable-compiler/Fable) project (MIT License).
    `Fable.Compiler` includes Fable's fork of FSharp.Compiler.Service.
  - Their NuGet dependencies, each under its own license.
- It modifies the downloaded binaries locally. It renames the FSharp.Compiler.Service fork to
  `Fable.FSharp.Compiler.Service` and retargets the Fable assemblies that reference it (`tools/Rename.fsx`).
- With `--hackable`, it clones Fable's source at the commit the pinned package was built from, and builds that
  source locally.

FSharp.Compiler.Service is part of the [F# compiler](https://github.com/dotnet/fsharp) (MIT License,
Copyright (c) Microsoft Corporation). Fable is Copyright (c) Fable contributors.

The results go into `vendor/`, `.work/` and `hackable/`. All three are gitignored and stay on your machine.
Do not commit or publish them. If you redistribute them anyway, the MIT license terms of Fable and of the F#
compiler apply, including keeping their copyright notices.

`tools/Rename.fsx` uses [Mono.Cecil](https://github.com/jbevain/cecil) (MIT License), which `dotnet fsi`
restores from NuGet.
