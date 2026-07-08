# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A monorepo of independent **.NET Aspire hosting helper** NuGet packages that fill gaps in the stock
Azure integrations. Each helper lives under `src/<short-name>/` (the `PackageId` with the shared
`Wicksipedia.Aspire.` prefix stripped — e.g. `Wicksipedia.Aspire.Hosting.Azure.SqlRoles` →
`src/Hosting.Azure.SqlRoles/`), ships as its own package, and has its own README **and its own
`CLAUDE.md`** — read the package-level `CLAUDE.md` before working inside a package.

**Hard constraint:** every helper depends only on **public** Aspire + `Azure.Provisioning` APIs — no
internal/reflection access — so it lifts out cleanly and could be contributed upstream. Preserve this when
editing; if you need an internal type, port a public equivalent instead.

## Commands

```bash
dotnet build                 # build the solution (Wicksipedia.Aspire.Helpers.slnx)
dotnet pack -c Release       # produce .nupkg per packable project
```

Requires the **.NET 10 SDK** (`global.json` pins `10.0.301`, `latestFeature` roll-forward). There is **no
test project** — no `dotnet test` target exists.

Publishing is manual: the `Publish` GitHub workflow (`workflow_dispatch`, gated on the `release`
environment reviewer) packs the solution, pushes to NuGet via OIDC, and tags `v<version>`. Don't publish
from a local machine.

## Repo-wide conventions

- **Central package management.** All dependency versions live in `Directory.Packages.props`; `.csproj`
  files carry `<PackageReference>` with **no** `Version`. Add/bump versions there, not in the project.
- **Shared metadata** (TFM `net10.0`, nullable, implicit usings, authors, license, packability) lives in
  `Directory.Build.props` — don't duplicate it per project.
- **Versioning:** a package's `major.minor` **tracks the Aspire version it targets** (`13.4.x` → Aspire
  13.4.x); the **patch** is the package's own revision. CI computes the full version as
  `<Aspire version from Directory.Packages.props>.<workflow run number>`; the `<Version>` in each `.csproj`
  is only a local-build fallback that CI overrides with `-p:Version=`.

## Adding a new helper package

1. `src/<short-name>/` (drop the `Wicksipedia.Aspire.` prefix) with a same-named `.csproj` that sets the
   full `<PackageId>` (metadata is otherwise inherited from `Directory.Build.props`), a `README.md`
   (`Pack="true"`), and a package-level `CLAUDE.md`.
2. Register dependency versions in `Directory.Packages.props`; reference them version-less in the `.csproj`.
3. Add the project to `Wicksipedia.Aspire.Helpers.slnx`.
4. Keep to the public-API-only constraint above.
