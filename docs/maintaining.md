# Maintaining Cella

The scripts in `tools/` need PowerShell 7 (`pwsh`).

## Versions and the changelog

`version.txt` holds the one version shared by the compiler, language server, standard library, launcher and editor plugins. Between releases it ends in `-dev`.

A commit adds a changelog entry by putting `[~]` in its description. Everything after the token is the entry, written in Markdown. Text before the token and commits without one are left out.

## Releasing

Releases are made from a clean `main`.

1. `./tools/release.ps1 prepare` collects the entries since the last tag into a new `CHANGELOG.md` section and removes `-dev` from `version.txt`. Pass `-Version 1.2.0` to release another number.
2. Edit `CHANGELOG.md` and commit it along with `version.txt`.
3. `./tools/release.ps1 publish` tags `v<version>`, pushes `main` and the tag together, and commits the next `-dev` version locally. Pass `-Next 2.0.0-dev` to choose that version. Push the commit when you're ready.

The tag starts `.github/workflows/release.yml`. It builds the components this release changed, runs the suites on Windows and Linux, and builds the version archives, the launcher and both editor plugins. It then publishes the release with `SHA256SUMS` and build attestations, and deploys `index.json` and the install scripts to GitHub Pages.

If the workflow fails:

1. Delete the draft release if one was created, then the tag: `git push origin :refs/tags/v<version>` and `git tag -d v<version>`.
2. Drop the local "Begin …" commit.
3. Commit the fix and run `publish` again.

The repository needs Pages set to deploy from GitHub Actions and release immutability turned on.

## Components

A component is a folder of files the compiler needs that doesn't come from this repository:

| Component | Holds | Installed to |
| --- | --- | --- |
| LLVM for each host (`win-x64`, `linux-x64`) | `lld`, `llvm-ar`, `llvm-lib` and the LLVM library the compiler loads | `llvm/<version>` |
| `windows-x64-gnu` | MinGW-w64 UCRT libraries, compiler-rt | `toolchains/windows-x64-gnu/<version>` |
| `windows-x64-msvc` | compiler-rt (the compiler finds Visual Studio and the Windows SDK itself) | `toolchains/windows-x64-msvc/<version>` |
| `linux-x64-gnu` | glibc 2.34 sysroot, compiler-rt | `toolchains/linux-x64-gnu/<version>` |

Each component has a recipe: `tools/components/llvm-<host>.ps1` for a host's LLVM and `tools/components/<target>.ps1` for a toolchain. Recipes download upstream files pinned by URL and SHA-256 in `tools/components/Upstream.ps1`. `components.json` names the release that last changed each component, with each host's LLVM under `llvm` and each target's toolchain under `toolchains`. A release builds the components listed with its own version and reuses the published archives of the rest.

Each version's bundle carries its own `components.json`, which names only its host's LLVM version and the toolchains. The release index lists the same `llvm` and `toolchains` entries for every version, with their URLs and checksums.

When you change a recipe or a pin, set that component's entry to the version being released. `prepare` warns about recipes that changed without one.

To build a component on its own, run:

```
./tools/build-component.ps1 -Kind toolchains -Name windows-x64-gnu -Output dist
```

The `linux-x64` LLVM (`-Kind llvm -Name linux-x64`) only builds on Linux.

To fill a folder with the components for development builds, run this and set `CELLA_HOME` to the folder:

```
./tools/fetch-components.ps1 -Output <folder>
```

It uses published archives where they exist and runs the recipes for the rest.

## Adding a target

1. Pin the upstream files in `tools/components/Upstream.ps1`.
2. Write `tools/components/<target>.ps1`. It takes `-Output` (the folder to fill) and `-Work` (a cache for downloads), and copies the libraries and their licenses into `-Output`.
3. Add `"<target>": "<next release>"` under `toolchains` in `components.json`.
4. In `Cella.Compiler`:
   - accept the name in `TargetTriple.Parse`;
   - add a `Toolchain` subclass in `Linking`, mapped in `Toolchain.FromTargetTriple`;
   - list the target in the `--target` usage text.
5. Add the target to `GoldenTests` and record expected outputs for it.

Toolchain archives are `.zip`. A host's bundle and its LLVM use the archive format listed for that host in `$Hosts`.

The launcher installs every target component a release lists, so it needs no change. CI rebuilds its cached toolchain whenever `components.json` or a recipe changes.

## Adding a host

A host is a system the compiler runs on, named by its .NET runtime identifier such as `linux-x64`.

1. Write a `tools/components/llvm-<host>.ps1` recipe and add `"<host>": "<next release>"` under `llvm` in `components.json`.
2. Add the host to `$Hosts` in `tools/Release.Common.ps1` with its archive format and executable extension.
3. Add a runner for it to the matrices in `release.yml` and `test.yml`.
4. Add an install script to `tools/install` and copy it into the site in `tools/write-index.ps1`.
