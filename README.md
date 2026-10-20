# DashyNMS Core

The shared code behind the DashyNMS desktop app, DashyNMS Mobile and the DashyNMS MCP server:
- a LibreNMS API client, with Graylog and Unimus clients;
- the models;
- alert counting, history and notification rules;
- graph styling and legends;
- sign-in helpers;
- the other logic every app needs to agree on.

It's plain .NET with no UI, targeting `net9.0` and `net10.0`, and is published as the `DashyNMS.Core` NuGet package on GitHub Packages.

## Using the package

Add the GitHub Packages feed to `nuget.config`:

```xml
<packageSources>
  <add key="dashynms" value="https://nuget.pkg.github.com/DashyNMS/index.json" />
</packageSources>
```

GitHub Packages needs a login even for public packages. Use a token with `read:packages` (for example `gh auth refresh -s read:packages`, then `gh auth token`), or `GITHUB_TOKEN` in GitHub Actions with `packages: read`.

## Building

```bash
dotnet test DashyNMS.Core.sln
```

Needs the .NET 9 and .NET 10 SDKs. CI runs the tests on Windows, macOS and Linux. A test that only holds on one platform is marked `[WindowsFact]` / `[NotWindowsFact]`.

## Releasing

**Development builds:** every merge to `main` (and a nightly run when something changed) publishes a prerelease: the version being built towards (`<Version>` in `Directory.Build.props`) plus `-ci.<run>`, for example `2.0.0-ci.42`. An app's development build can pin one to test unreleased Core. The workflow summary says which commit each one was built from.

**Releases:** push a tag such as `v2.0.0`. The release workflow tests, packs that version and publishes it to GitHub Packages. Then raise `<Version>` to the next version being worked on, so later development builds sort after the release. Versions follow semver: a breaking change to the public API, or to anything persisted in settings, is a major bump.

## History

Core moved here from [DashyNMS/desktop](https://github.com/DashyNMS/desktop) in October 2026 (desktop#248), with its history. Issue numbers in older comments and commits (`#169`, `#266`…) refer to DashyNMS/desktop.

The package is `DashyNMS.Core`, but the assembly and namespaces are still `DesktopNMS.Core`. Renaming those is a separate, later breaking change.

## Licence

[MIT](LICENSE).