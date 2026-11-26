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
| `docs/` | This guide, and the notes for each version in `docs/releases` |

## Builds and releases

Every DashyNMS repository builds the same way.

| Workflow | When | Version |
| --- | --- | --- |
| **CI** (`ci.yml`) | every pull request, and first in each build below | none |
| **Edge build** (`edge-build.yml`) | every commit to `main` | `2.0.0-edge.<build>` |
| **Nightly build** (`nightly-build.yml`) | 00:00 UTC (GitHub may start it late) if `main` changed since the last nightly, or by hand | `2.0.0-nightly.<build>` |
| **Release build** (`release-build.yml`) | a tag `v2.0.0` is pushed | `2.0.0` |
| **CodeQL** and **Dependency submission** | as their names say | none |

The three builds call `package.yml`, which packs `DashyNMS.Core`, attests its provenance and publishes it to GitHub Packages. `<build>` is the workflow's run number. A published version is never pushed over: if an edge or nightly publish fails part way, run the build again by hand. Check a package with `gh attestation verify <file> --repo DashyNMS/core`.

`2.0.0-ci.N` packages came from the development builds this replaced. They rank below every edge package, and stay available to the apps that pin them.

Renaming a workflow file starts its run number again from 1, which would repeat published versions. Don't rename `edge-build.yml` or `nightly-build.yml` without raising `<Version>` at the same time.

**Releasing:** check that `docs/releases/<version>.md` reads well, then tag `main`'s newest commit and push only the tag:

```bash
git tag v2.0.0 origin/main && git push origin v2.0.0
```

The tag has to match `<Version>` in `Directory.Build.props`. Don't create the release on GitHub first, because everyone watching would be told about an empty one: the build creates it at the end, with its [notes](#release-notes). Then raise `<Version>`, so later edge and nightly builds rank above the release.

If it fails there's no release:

- **Nothing on `main` has to change:** run **Release build** by hand with the tag. The package, if it was already pushed from that commit, is kept after its provenance is checked.
- **It needs a fix:** the tag has to be on `main`'s newest commit, so merge the fix, then move the tag: `git push --delete origin v2.0.0`, `git fetch origin`, `git tag -f v2.0.0 origin/main` and `git push origin v2.0.0`. If the package was already pushed, that version is used up: raise `<Version>` first.

### Release notes

Every DashyNMS repository writes its release notes the same way. **`docs/releases/<version>.md` is the one set of notes for the version in `Directory.Build.props`.** It covers everything since the last release and grows as changes merge. Nothing else is written by hand: not a GitHub release's text, and not a file for each nightly.

**In the pull request that makes the change:**

- If a user of this repository would notice it, add or update its line in that file. A `feat`, `fix` or `perf` nearly always needs one. `ci`, `test`, `docs`, `chore`, `refactor` and dependency updates don't, unless someone would notice.
- Put it under `## Breaking`, `## New`, `## Changed` or `## Fixed`, in that order, as a list item: `- **What it's about:** what's different now, in plain words (#NN).` Other headings fail CI.
- Say what's true of the release as a whole. If a later change alters or undoes something that hasn't been released, edit its line rather than adding another. A fix to something that has never been released has no line.
- Breaking changes say what the reader has to do.
- Never edit a released version's file. Never put download tables, filenames or build numbers in it: they differ for each build.

**What the builds do with it:**

| Build | Notes |
| --- | --- |
| CI | fails if the file is missing, empty or has other headings |
| Edge | a tag and prerelease with links to the file and to the changes since the last edge build |
| Nightly | a tag and prerelease with a copy of the file, and a link to the changes since the last build |
| Release | the last step creates the release with the file as its notes |

**Raising `<Version>`:** the same pull request adds `docs/releases/<new version>.md` with its first line, because CI won't pass without it.

## Commits, pull requests and issues

Dependabot uses `build(deps): ...` for every ecosystem. Its generated dependency names and grouped summaries may exceed the usual title length.

The same standard as every DashyNMS repository: `type(scope): what changes, in plain words`.

- **Types:** `feat`, `fix`, `perf`, `refactor`, `docs`, `test`, `build`, `ci`, `chore`, `revert`.
- **Scopes:** `api`, `alerting`, `rules`, `sensors`, `graphs`, `services`, `sign-in`, `settings`, `json`, `licences`, `intelligence`, `help`, `demo`, `deps`.
- **The description:** lower-case start, no full stop, under about 72 characters.
- **Issues:** issue titles use the same form, worded as the result the fix delivers.
- **PRs:** one issue per PR, squash-merged, with `Closes #NN` in the description.
- **Breaking changes:** add `!` after the type or scope.
- **Release notes:** a change users would notice adds its line to `docs/releases/<version>.md` in the same PR. See [Release notes](#release-notes).

Every PR says what desktop, mobile and MCP need to do about it.

## Repository standards

See [Repository standards](REPOSITORY-STANDARDS.md) for security gates, shared
policy ownership, contribution files and the procedure for updating copied helpers.
