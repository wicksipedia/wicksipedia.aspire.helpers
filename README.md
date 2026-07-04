# Wicksipedia.Aspire.Helpers

A small monorepo of [.NET Aspire](https://learn.microsoft.com/dotnet/aspire) hosting helpers that fill
gaps in the stock Azure integrations. Each helper is an independent, self-contained NuGet package that
depends only on **public** Aspire + `Azure.Provisioning` APIs — so it lifts out cleanly (and could be
contributed back upstream).

## Helpers

| Package | What it does |
|---|---|
| [`Wicksipedia.Aspire.Helpers.SqlRoles`](src/Wicksipedia.Aspire.Helpers.SqlRoles/) | Grant Azure SQL managed identities **least-privilege** database roles (e.g. `db_datareader`) instead of Aspire's hardcoded `db_owner`-for-every-identity — including the private-endpoint grant-script plumbing. |

More will land here over time.

## Building

```bash
dotnet build
```

Each helper lives under `src/<PackageId>/` with its own README. Requires the .NET 10 SDK (matching
Aspire 13.4.x).

## License

[MIT](LICENSE) © Matt Wicks
