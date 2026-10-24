<p align="center">
  <img src="https://dashynms.pckp.net/assets/img/favicon.svg" alt="" width="96" height="96">
</p>

<h1 align="center">DashyNMS Core</h1>

<p align="center">
  <strong>The shared heart of every DashyNMS app.</strong><br>
  One LibreNMS client and one set of rules, so desktop, mobile and the MCP server agree.
</p>

<p align="center">
  <a href="https://dashynms.pckp.net/">Website</a> ·
  <a href="https://github.com/orgs/DashyNMS/packages?repo_name=core">Package</a> ·
  <a href="https://github.com/DashyNMS/core/milestones">Roadmap</a>
</p>

---

DashyNMS Core is the plain .NET library behind [DashyNMS desktop](https://github.com/DashyNMS/desktop), [DashyNMS Mobile](https://github.com/DashyNMS/mobile) and [DashyNMS MCP](https://github.com/DashyNMS/mcp). It has no UI. Each app adds its own screens on top, so an alert is counted, named and notified the same way everywhere.

## What's in it

- **LibreNMS, Graylog and Unimus clients:** the API, with LibreNMS's quirks handled once (404s that mean "none", stringly-typed numbers, permission errors that mean "not this token").
- **Alerting:** change detection between polls, counting, history, notification rules, and the rule builder's field catalogue.
- **Monitors and caches:** the alert, device and sensor pollers, the device and rule caches, and the dashboard layout. Each app supplies when it's in the background and whether it's signed in.
- **Graphs:** LibreNMS's graphs restyled to the app's theme, with the app's own legends.
- **Sensor definitions:** optional OS-sharded data with exact and source-derived type/context selectors. Unknown values, missing metadata and conflicting mappings remain unresolved.
- **Sign-in, settings and the rest:** sign in with LibreNMS's own page, settings and the starter dashboard, and the open-source notices every app shows.

## Use it

Core is published as the **`DashyNMS.Core`** package on GitHub Packages, for `net9.0` and `net10.0`. Add the feed to `nuget.config`:

```xml
<packageSources>
  <add key="dashynms" value="https://nuget.pkg.github.com/DashyNMS/index.json" />
</packageSources>
```

GitHub Packages needs a login even for public packages: a token with `read:packages` (locally, `gh auth refresh -s read:packages`, then `gh auth token`; in GitHub Actions, `GITHUB_TOKEN` with `packages: read`).

**Versions**
- **Releases**, for example `2.0.0`, follow semver. A breaking change to the public API, or to anything saved in settings, is a major version.
- **Development builds**, `2.0.0-ci.N`, come from every merge to `main`, so an app can test unreleased Core.

The assembly and namespaces are still `DesktopNMS.Core`; renaming them is [#1](https://github.com/DashyNMS/core/issues/1).

Sensor definition readers accept schemas 1 and 2. They prefer the separate v2 feed, falling back to v1 only when v2 returns 404. Existing `Resolve` and `ApplyAsync` calls remain supported. Consumers with device metadata should use `Resolve(sensor, os, device)` or `ApplyWithDevicesAsync` so hardware/version conditions can be evaluated. `ResolveDetailed` and `SensorStateResolution.Outcome` distinguish unavailable data, coverage gaps, unknown enum values and conflicts. `SensorMonitor` supplies cached device metadata automatically; Desktop and MCP need the updated Core package, with no vendor-specific matching code.

## Build it

You need the .NET 9 and .NET 10 SDKs.

```bash
git clone https://github.com/DashyNMS/core.git
cd core
dotnet test DashyNMS.Core.sln
```

CI runs the tests on Windows, macOS and Linux. Mark a test that only holds on one platform with `[WindowsFact]` or `[NotWindowsFact]`.

**Releasing:** push a tag such as `v2.0.0`. The release workflow tests, packs and publishes that version. Then raise `<Version>` in `Directory.Build.props` to the next version, so later development builds sort after the release.

## Commits, pull requests and issues

The same standard as every DashyNMS repository: `type(scope): what changes, in plain words`.
- **Types:** `feat`, `fix`, `perf`, `refactor`, `docs`, `test`, `build`, `ci`, `chore`, `revert`.
- **Scopes:** `api`, `alerting`, `rules`, `sensors`, `graphs`, `services`, `sign-in`, `settings`, `json`, `licences`.
- **The description:** lower-case start, no full stop, under about 72 characters.
- **Issues:** issue titles use the same form, worded as the result the fix delivers.
- **PRs:** one issue per PR, squash-merged, with `Closes #NN` in the description.
- **Breaking changes:** add `!` after the type or scope.

Every PR says what desktop, mobile and MCP need to do about it.

## Help and feedback

- Found a bug or have an idea? [Open an issue](https://github.com/DashyNMS/core/issues). Planned work is under the [milestones](https://github.com/DashyNMS/core/milestones).
- Issue numbers in older code comments and commits (`#169`, `#266`…) refer to [DashyNMS/desktop](https://github.com/DashyNMS/desktop), where Core lived until October 2026 (desktop#248).

---

<sub>DashyNMS isn't affiliated with LibreNMS, Graylog or Unimus, which are trademarks of their respective owners.<br>
MIT licence: see [LICENSE](LICENSE).</sub>
