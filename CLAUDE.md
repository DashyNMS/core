# DashyNMS Core

Shared, UI-free .NET code for the DashyNMS desktop app, DashyNMS Mobile and the MCP server. It targets `net9.0` and `net10.0` and is published as the `DashyNMS.Core` package on GitHub Packages (see README.md).

- Every change is a consumer-facing API change. Say in the PR what desktop, mobile and MCP need to do, and bump the version accordingly (semver; a breaking change is a major bump).
- Tests must pass on Windows, macOS and Linux. Mark platform-specific tests with `[WindowsFact]` / `[NotWindowsFact]`.
- Never put real device names, hostnames, addresses or other identifiable network data in issues, PRs, commits or test fixtures. Use `example.net` and documentation address ranges.

## Commits and pull requests

Same standard as DashyNMS/desktop: `type(scope): what changes, in plain words`.
- Types: `feat`, `fix`, `perf`, `refactor`, `docs`, `test`, `build`, `ci`, `chore`, `revert`.
- Scope: the area, for example `api`, `alerting`, `graphs`, `signin`, `json`.
- Lower-case start, no full stop, under about 72 characters.
- `Closes #NN` goes in the description.
- A breaking API change gets `!`.
- One issue per PR, squash-merged.