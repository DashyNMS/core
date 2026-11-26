# DashyNMS Core

Shared, UI-free .NET library for the DashyNMS desktop app, DashyNMS Mobile and the MCP server. It targets `net9.0` and `net10.0` and is published as the `DashyNMS.Core` package on GitHub Packages; the assembly and namespaces are still `DesktopNMS.Core` (#1).

- Every change is a consumer-facing API change. Say in the PR what desktop, mobile and MCP need to do, and version accordingly with semver; a breaking change is a major bump.
- Front-end concerns stay out: no UI types or dispatcher. Events are raised on the thread that does the work; each app marshals.
- Use sealed classes and XML doc comments explaining why.

## Repository conventions

- Before changing build, test or publication workflows, read [the developer guide](docs/DEVELOPMENT.md). Before changing release notes, read its [Release notes](docs/DEVELOPMENT.md#release-notes) section.
- Before changing shared conventions or security checks, read [Repository standards](docs/REPOSITORY-STANDARDS.md); update the canonical source and every affected consumer.
- Create or find an issue describing the bug or intended change before opening any PR, including dependency, documentation and CI changes. Every PR must reference its issue.
- Work on a branch; use one issue per PR into `main`, then squash-merge. The PR title becomes the commit subject on `main`.
- PR titles, branch commits and issue titles use `type(scope): what changes, as the user would notice it`. Read [Commits, pull requests and issues](docs/DEVELOPMENT.md#commits-pull-requests-and-issues) for this repository's types, scopes and examples.
- Start the description in lower case, omit the final full stop and aim for a complete subject under 72 characters. Separate two scopes with a comma; omit the scope when there is no single area. Document a new scope in the developer guide in the same PR.
- Word issue titles as the result the fixing PR delivers. Epics end with `(epic)`. Keep issue numbers out of titles; start the PR body with `Closes #NN` when resolving an issue, then explain what changes and how it was checked.
- Mark breaking changes with `!` and explain the migration and version implications for affected consumers.
- Dependabot uses `build(deps): ...` for every ecosystem. Its generated dependency names and grouped summaries may exceed the usual title length.
- Use British spelling in user-facing text. Keep real device names, hostnames, addresses, credentials and other identifiable network data out of issues, PRs, commits, logs, screenshots and fixtures. Use `example.net` and documentation address ranges.
- Commits, PRs and release notes carry no AI attribution or `Co-Authored-By` lines.
- Read `LICENSE` before changing or redistributing repository material. Preserve third-party licences, copyright notices and provenance.

## Validation and releases

- Tests must pass on Windows, macOS and Linux with `dotnet test DashyNMS.Core.slnx`. Mark platform-specific tests with `[WindowsFact]` or `[NotWindowsFact]`.
- Every commit to `main` publishes an edge package. Read [Builds and releases](docs/DEVELOPMENT.md#builds-and-releases) before publishing or changing version handling.
- A change consumers would notice adds or updates its line in `docs/releases/<version>.md`, using the version in `Directory.Build.props`, in the same PR. GitHub release notes come from that file.
