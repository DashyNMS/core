# Developing DashyNMS Core

## Requirements

The .NET 10 SDK, and the .NET 9 runtime for the `net9.0` tests.

## Build and test

```bash
git clone https://github.com/DashyNMS/core.git
cd core
dotnet test DashyNMS.Core.slnx
```

CI runs the tests on Windows, macOS and Linux. Mark a test that only holds on one platform with `[WindowsFact]` or `[NotWindowsFact]`.

## Layout

| Folder | What's in it |
| --- | --- |
| `src/` | The library |
| `tests/` | Its tests |
| `tools/` | Programs run by hand, such as the alert condition catalogue |
| `scripts/` | What the workflows run |
| `docs/` | This guide |

## Builds and releases

Every DashyNMS repository builds the same way.

| Workflow | When | Version |
| --- | --- | --- |
| **CI** (`ci.yml`) | every pull request, and first in each build below | none |
| **Edge build** (`edge-build.yml`) | every commit to `main` | `2.0.0-edge.<build>` |
| **Nightly build** (`nightly-build.yml`) | 00:00 UTC (GitHub may start it late) if `main` changed since the last nightly, or by hand | `2.0.0-nightly.<build>` |
| **Release build** (`release-build.yml`) | a GitHub release is published | `2.0.0` |
| **CodeQL** and **Dependency submission** | as their names say | none |

The three builds call `package.yml`, which packs `DashyNMS.Core`, attests its provenance and publishes it to GitHub Packages. `<build>` is the workflow's run number. A published version is never pushed over: if an edge or nightly publish fails part way, run the build again by hand. Check a package with `gh attestation verify <file> --repo DashyNMS/core`.

`2.0.0-ci.N` packages came from the development builds this replaced. They rank below every edge package, and stay available to the apps that pin them.

Renaming a workflow file starts its run number again from 1, which would repeat published versions. Don't rename `edge-build.yml` or `nightly-build.yml` without raising `<Version>` at the same time.

**Releasing:** publish a GitHub release on `main`'s newest commit, with the tag `v<version>` matching `<Version>` in `Directory.Build.props`. It goes back to being a draft while the package is built, and is published again when that's done. Then raise `<Version>`, so later edge and nightly packages rank above the release.

If a release fails it stays a draft. Don't publish the draft by hand, because that starts the build again:

- **Nothing on `main` has to change:** run **Release build** by hand with the tag. A package that was already pushed from that commit is kept, after its provenance is checked.
- **It needs a fix:** the release has to be `main`'s newest commit, so merge the fix, delete the draft with its tag (`gh release delete v2.0.0 --cleanup-tag`) and publish it again. If its package was already pushed, that version is used up: raise `<Version>` first.

## Commits, pull requests and issues

The same standard as every DashyNMS repository: `type(scope): what changes, in plain words`.

- **Types:** `feat`, `fix`, `perf`, `refactor`, `docs`, `test`, `build`, `ci`, `chore`, `revert`.
- **Scopes:** `api`, `alerting`, `rules`, `sensors`, `graphs`, `services`, `sign-in`, `settings`, `json`, `licences`, `intelligence`.
- **The description:** lower-case start, no full stop, under about 72 characters.
- **Issues:** issue titles use the same form, worded as the result the fix delivers.
- **PRs:** one issue per PR, squash-merged, with `Closes #NN` in the description.
- **Breaking changes:** add `!` after the type or scope.

Every PR says what desktop, mobile and MCP need to do about it.
