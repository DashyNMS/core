# DashyNMS Core

Shared, UI-free .NET library for the DashyNMS desktop app, DashyNMS Mobile and the MCP server. It targets `net9.0` and `net10.0` and is published as the `DashyNMS.Core` package on GitHub Packages; the assembly and namespaces are still `DesktopNMS.Core` (#1).

- Every change is a consumer-facing API change. Say in the PR what desktop, mobile and MCP need to do, and version accordingly (semver; a breaking change is a major bump). Merges to `main` publish `2.0.0-ci.N` development builds.
- Tests must pass on Windows, macOS and Linux (`dotnet test DashyNMS.Core.sln`). Mark platform-specific tests with `[WindowsFact]` / `[NotWindowsFact]`.
- Front-end concerns stay out: no UI types, no dispatcher. Events are raised on the thread that does the work; each app marshals.
- Never put real device names, hostnames, addresses or other identifiable network data in issues, PRs, commits or test fixtures. Use `example.net` and documentation address ranges.
- Match desktop's style: British spelling in user-facing text, XML doc comments explaining *why*, sealed classes.

## Commits, pull requests and issues

- Work on a branch, open a PR into `main` (one issue per PR), and squash-merge.
- PR titles, commits and issue titles: `type(scope): what changes, in plain words`.
  - Types: `feat`, `fix`, `perf`, `refactor`, `docs`, `test`, `build`, `ci`, `chore`, `revert`.
  - Scopes: `api`, `alerting`, `rules`, `sensors`, `graphs`, `services`, `sign-in`, `settings`, `json`, `licences`.
  - Lower-case start, no full stop, under about 72 characters, describing the result.
  - `Closes #NN` goes in the PR description, not the title.
  - A breaking change gets `!`.
  - Issue titles are worded as the result the fixing PR delivers.
- No AI attribution or `Co-Authored-By` lines in commits, PRs or release notes.