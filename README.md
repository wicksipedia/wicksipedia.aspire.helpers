# Wicksipedia.Aspire.Helpers

A small monorepo of [Aspire](https://aspire.dev) hosting helpers that fill
gaps in the stock Azure integrations. Each helper is an independent, self-contained NuGet package that
depends only on **public** Aspire + `Azure.Provisioning` APIs — so it lifts out cleanly (and could be
contributed back upstream).

## Why Aspire?

[Aspire](https://aspire.dev) is how we build cloud-native .NET now. One AppHost
model describes the whole system — the app, its dependencies (SQL, Redis, Service Bus…), and how they wire
together — and Aspire turns that into a local run (service discovery, containers, injected connection
strings, health checks; no Docker Compose, no YAML) **and** the Azure Bicep to deploy it. The
infrastructure stops being a separate, hand-maintained artifact and becomes C# that lives next to the app.

At [SSW](https://www.ssw.com.au) that shift has been a real accelerant. Across 1,000+ enterprise engagements
the slow part was rarely the app — it was standing up and re-standing-up the surrounding infrastructure for
each client. Aspire collapses that: the same model gives every project a consistent local F5 experience and
a repeatable path to Azure, so teams spend their time on the domain, not on plumbing. It's our
[recommended default for cloud-native .NET](https://www.ssw.com.au/rules/aspire/), baked into the
[SSW.CleanArchitecture](https://github.com/SSWConsulting/SSW.CleanArchitecture) template that most new
projects start from.

Which is exactly why the last mile matters. Aspire's convenience is only worth having if what it provisions
is production-safe — and out of the box a few defaults aren't (every database identity gets `db_owner`;
grant scripts assume public network access). **These helpers harden that generated infrastructure** —
least-privilege database roles, private-endpoint plumbing — so the "it just works" path doesn't quietly
ship over-privileged, publicly-exposed resources to a client.

## Helpers

| Package | What it does |
|---|---|
| [`Wicksipedia.Aspire.Hosting.Azure.SqlRoles`](src/Hosting.Azure.SqlRoles/) | Grant Azure SQL managed identities **least-privilege** database roles (e.g. `db_datareader`) instead of Aspire's hardcoded `db_owner`-for-every-identity — including the private-endpoint grant-script plumbing. |

More will land here over time.

## Building

```bash
dotnet build
```

Each helper lives under `src/<PackageId>/` with its own README. Requires the .NET 10 SDK (matching Aspire 13.4.x).

## Publishing

Every helper is a packable NuGet project. To publish one to [nuget.org](https://www.nuget.org) and use trusted publishers.

```bash
dotnet pack -c Release
```

A package's `major.minor` tracks the Aspire version it targets (e.g. `13.4.x` → Aspire 13.4.x); the patch
is the package's own revision.

## License

[MIT](LICENSE) © Matt Wicks
