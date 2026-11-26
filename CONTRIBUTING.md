# Contributing

Bug reports, accessibility reports and ideas are welcome. Open an issue before
preparing a code change so the maintainer can agree its scope.

Use the issue forms, include the version and platform, and check for an existing
report first. Follow [the code of conduct](CODE_OF_CONDUCT.md). Report security
problems privately under [SECURITY.md](SECURITY.md). Use synthetic names and
documentation addresses; exclude credentials and identifiable network details.

## Changes and validation

Read [the developer guide](docs/DEVELOPMENT.md) for setup, build requirements,
release notes and the [title standard](docs/DEVELOPMENT.md#commits-pull-requests-and-issues).
Work on a branch into `main`, with one issue per PR. Start the body with
`Closes #NN` when resolving an issue and describe behaviour, validation and
consumer impact. Maintainers squash-merge PRs. Commits, PRs and release notes
carry no AI attribution or co-author lines.

Run `dotnet test DashyNMS.Core.slnx`. Also run the repository policy and security checks in
[Repository standards](docs/REPOSITORY-STANDARDS.md). Report checks that were
unavailable; platform-specific builds still require their own CI results.

Explain what desktop, mobile and MCP consumers must do. Public contract changes need semver and migration review. Keep UI adapters outside Core.

Add release notes when users or consumers would notice the change. Published
versions stay immutable. Project-owned material uses the MIT licence in
[LICENSE](LICENSE); retain upstream terms, notices and provenance.

Every PR must reference an existing issue describing the bug or intended change.
Create or find that issue before opening the PR, including for documentation,
dependency and CI changes. Use a non-closing reference for partial work.
