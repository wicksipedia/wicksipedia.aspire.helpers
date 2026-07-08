# Hosting.Azure.SqlRoles

Grant Azure SQL user-assigned managed identities **specific, least-privilege database roles** from your
Aspire AppHost — instead of Aspire's built-in behaviour of granting **`db_owner` to every
referencing identity**.

## Why

Aspire's `Aspire.Hosting.Azure.Sql` integration creates each managed identity's database user with a
PowerShell deployment script that **hardcodes** a single grant:

```sql
ALTER ROLE db_owner ADD MEMBER [<identity>];
```

Every app that touches the database gets `db_owner`. The only public knob is
`ClearDefaultRoleAssignments()`, which is all-or-nothing — there's no way to say "the API only needs
read/write, the migrator needs owner". So a least-privilege setup isn't possible with the box.

This helper disables that built-in grant and emits its **own** deployment scripts — one per identity, per
database — that create the identity's user and add it to **exactly the roles you ask for**, idempotently.

> [!TIP]
> SSW's rule [Do you use Managed Identities in your Azure projects?](https://www.ssw.com.au/rules/use-managed-identity-in-azure)
> makes the case for managed identities on the principle that *"each identity gets only the permissions it
> needs"* — assigning granular Azure RBAC roles (`Storage Blob Data Reader`, `Key Vault Secrets User`)
> rather than a shared, over-scoped secret. That reasoning stops at the Azure IAM boundary: **inside** the
> database, Aspire hands every identity `db_owner`, so an app that only reads gets full ownership — exactly
> the over-privilege the rule warns against, one layer down. This package carries least-privilege past the
> IAM boundary into the database itself: the same identity that has a narrow Azure role now gets an
> equally narrow **database** role (`db_datareader`, not `db_owner`).

## Install

```bash
dotnet add package Wicksipedia.Aspire.Hosting.Azure.SqlRoles
```

## Use

The extension methods live in the `Aspire.Hosting` namespace (the Aspire convention), so they light up on
your existing `using Aspire.Hosting;`.

```csharp
var sql = builder.AddAzureSqlServer("sql");
var db  = sql.AddDatabase("appdb");

var idMigrator = builder.AddAzureUserAssignedIdentity("id-migrator");
var idApi      = builder.AddAzureUserAssignedIdentity("id-api");

migrator.WithAzureUserAssignedIdentity(idMigrator);
api.WithAzureUserAssignedIdentity(idApi);

sql.WithSqlDatabaseRoles(idMigrator, SqlDatabaseRole.DbOwner)
   .WithSqlDatabaseRoles(idApi, SqlDatabaseRole.DbDataReader, SqlDatabaseRole.DbDataWriter);
```

The first `WithSqlDatabaseRoles` call on a server clears Aspire's default `db_owner` grant. Any identity
you **don't** list gets **no** database access — least-privilege by default: it simply fails to connect,
which is the safe direction to break in.

### Supported roles

`SqlDatabaseRole`: `DbOwner`, `DbDataReader`, `DbDataWriter`, `DbDdlAdmin`, `DbSecurityAdmin`,
`DbAccessAdmin`.

### Private endpoints (SQL with public network access disabled)

The grant scripts run on Azure Container Instances. When the SQL server sits behind a **private endpoint**
(public access disabled), that ACI must run **inside your VNet** and reach a storage file share. Aspire
builds this plumbing for its own grant script, but `ClearDefaultRoleAssignments()` tears it down — so this
helper reproduces it. Hand it a subnet + storage account with `WithGrantScriptNetwork` and it wires the
storage **file** private endpoint, an **NSG** (outbound 443 → Entra ID + SQL), the ACI **subnet
delegation** to container groups, and the SQL admin identity's **file-share role**:

```csharp
var vnet     = builder.AddAzureVirtualNetwork("vnet");
var peSubnet = vnet.AddSubnet("private-endpoints", "10.0.2.0/27");
peSubnet.AddPrivateEndpoint(sql);                        // disables SQL public access

var scriptSubnet  = vnet.AddSubnet("grant-scripts", "10.0.2.32/27");
var scriptStorage = builder.AddAzureStorage("grantscripts");

sql.WithGrantScriptNetwork(scriptSubnet, scriptStorage)  // call AFTER AddPrivateEndpoint(sql)
   .WithSqlDatabaseRoles(idMigrator, SqlDatabaseRole.DbOwner)
   .WithSqlDatabaseRoles(idApi, SqlDatabaseRole.DbDataReader, SqlDatabaseRole.DbDataWriter);
```

- `WithGrantScriptNetwork` is a **no-op** when the server has public access — the plain scripts run as-is,
  so it's safe to leave in place for both public and private topologies.
- Call it **after** `AddPrivateEndpoint(sql)`: the plumbing is wired eagerly against the private endpoint
  already in the model.
- The grant-script ACI mounts the storage file share with the account key, so the helper enables
  `AllowSharedKeyAccess` on the storage account for you.

## How it works

- **Publish-only.** Under `aspire run` (a local `RunAsContainer` SQL, which uses SQL auth) it's a no-op.
- On the first `WithSqlDatabaseRoles` for a server: `ClearDefaultRoleAssignments()` + a dedicated
  `<sql>-roles` infrastructure module.
- Per identity, per database: an `AzurePowerShellScript` that runs as the SQL server's **Entra admin**
  identity (which `AddAzureSqlServer` provisions), creates the identity's user
  (`CREATE USER … WITH SID …, TYPE = E`) and adds it to each requested role (`ALTER ROLE … ADD MEMBER`).
  Idempotent, with a retry loop, so redeploys are safe.

## Versioning

The package version's **`major.minor` tracks the Aspire version it targets** — `13.4.x` targets Aspire
`13.4.x` — and the **patch** is this package's own revision (fixes/tweaks against that Aspire line). So
`13.4.0` is the first release for Aspire 13.4; `13.4.1` would be a later fix still on 13.4; a bump to
Aspire 13.5 ships as `13.5.0`. Pick the package `major.minor` that matches your Aspire packages.

## Requirements

- Aspire **13.4.x** (net10.0).
- The private-endpoint path uses Aspire's still-**evaluation-only** VNet/private-endpoint APIs, so the
  package suppresses `ASPIREAZURE003`. Re-verify on Aspire upgrades — the ported plumbing tracks Aspire
  internals.
- The Azure SQL server must have an Entra admin (Aspire's `AddAzureSqlServer` sets one up).

## Ported from / how to retire this package

The grant script and private-endpoint plumbing are ported from Aspire's own Azure SQL integration, pinned
at commit
[`f0635396`](https://github.com/dotnet/aspire/blob/f06353968ad04165cb6d45f112174208b4ee60f7/src/Aspire.Hosting.Azure.Sql/AzureSqlServerResource.cs)
(`AzureSqlServerResource.cs` — the grant script + `OnPrivateEndpointCreated` /
`PrepareDeploymentScriptInfrastructure`; and
[`AzureSqlExtensions.cs`](https://github.com/dotnet/aspire/blob/f06353968ad04165cb6d45f112174208b4ee60f7/src/Aspire.Hosting.Azure.Sql/AzureSqlExtensions.cs)).
Internal types were swapped for public equivalents.

**The upstream change that would make this package unnecessary:** have Aspire's `AddRoleAssignments` build
the `ALTER ROLE` lines from the requested roles (map `RoleDefinition` → database role) instead of
hardcoding `db_owner`, and expose a public `WithReference(db, params SqlDatabaseRole[])` (or
`WithSqlDatabaseRoleAssignments`) that seeds them. If that lands upstream, drop the dependency.

## License

[MIT](../../LICENSE).
