# Wicksipedia.Aspire.Helpers

A small monorepo of [.NET Aspire](https://learn.microsoft.com/dotnet/aspire) hosting helpers that fill
gaps in the stock Azure integrations. Each helper is an independent, self-contained NuGet package that
depends only on **public** Aspire + `Azure.Provisioning` APIs — so it lifts out cleanly (and could be
contributed back upstream).

## Helpers

| Package | What it does |
|---|---|
| [`Wicksipedia.Aspire.Hosting.Azure.SqlRoles`](src/Wicksipedia.Aspire.Hosting.Azure.SqlRoles/) | Grant Azure SQL managed identities **least-privilege** database roles (e.g. `db_datareader`) instead of Aspire's hardcoded `db_owner`-for-every-identity — including the private-endpoint grant-script plumbing. |

More will land here over time.

## Building

```bash
dotnet build
```

Each helper lives under `src/<PackageId>/` with its own README. Requires the .NET 10 SDK (matching
Aspire 13.4.x).

## Publishing

Every helper is a packable NuGet project. To publish one to [nuget.org](https://www.nuget.org):

```bash
dotnet pack -c Release
dotnet nuget push src/<PackageId>/bin/Release/*.nupkg -k <api-key> -s https://api.nuget.org/v3/index.json
```

A package's `major.minor` tracks the Aspire version it targets (e.g. `13.4.x` → Aspire 13.4.x); the patch
is the package's own revision.

## License

[MIT](LICENSE) © Matt Wicks
