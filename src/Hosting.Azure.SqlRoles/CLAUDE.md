# CLAUDE.md — Hosting.Azure.SqlRoles

Package-specific guidance. See the repo-root `CLAUDE.md` for monorepo-wide conventions (build, central
package management, versioning). Read `README.md` here for the user-facing API and rationale.

## Purpose

Replaces Aspire's built-in "grant `db_owner` to every referencing identity" with per-identity,
least-privilege database-role grants (`db_datareader`, `db_datawriter`, …). The entire package is one
file: `AzureSqlDatabaseRoleExtensions.cs`.

## Architecture — read before editing

- **Publish-only.** Every public method early-returns unless `ExecutionContext.IsPublishMode`. Under
  `aspire run` (local `RunAsContainer` SQL, SQL auth) it's a no-op.
- **Deferred emission.** `WithSqlDatabaseRoles` / `WithGrantScriptNetwork` only record intent into a
  per-server `GrantState` held in a `ConditionalWeakTable`. The Bicep is emitted at publish time by the
  `<sql>-roles` `AddAzureInfrastructure` callback (`EmitGrantScripts`), which runs **after** all grants and
  `AddDatabase` calls — so the order of user calls doesn't matter, except the private-endpoint rule below.
- **First-grant side effect.** The first `WithSqlDatabaseRoles` on a server calls
  `ClearDefaultRoleAssignments()` (kills Aspire's `db_owner` script) and registers the roles module.
- **Grant script** (`BuildScript`): PowerShell talking to `Microsoft.Data.SqlClient` directly, one per
  identity per database, run on ACI as the SQL server's **Entra admin** identity (which
  `AddAzureSqlServer` provisions). Idempotent (`IF IS_ROLEMEMBER … = 0`) with a retry loop.
  **Never call `Invoke-Sqlcmd` here.** It registers Always Encrypted key-store providers on every call,
  which throws `MissingMethodException` whenever the deployment-script image ships a different
  `Microsoft.Extensions` assembly set. The `SqlServer` module is still installed, for its
  `Microsoft.Data.SqlClient.dll` only, and its version is pinned deliberately (aspire#9926) —
  **don't unpin**.
- **Private-endpoint path** (`WithGrantScriptNetwork` → `ConfigurePrivateEndpointPlumbing`): when SQL is
  behind a private endpoint the ACI must run in-VNet. Wires a storage **file** private endpoint, an NSG
  (outbound 443 → Entra ID + SQL), ACI subnet delegation, and the admin identity's file-share role.
  **No-op** under public network access. Must be called **after** `AddPrivateEndpoint(sql)` — the plumbing
  is wired eagerly against the private endpoint already in the model (a later event hits a read-only
  container). Also flips `AllowSharedKeyAccess` on the storage account (the ACI mounts the share by key).

## Gotchas

- Uses Aspire's **evaluation-only** VNet APIs, hence `NoWarn` includes `ASPIREAZURE003`.
- The grant script + private-endpoint plumbing are **ported** from Aspire's own Azure SQL integration,
  pinned at a specific commit (see README "Ported from"). Internal Aspire types were swapped for public
  equivalents (e.g. the `StorageFiles` private-endpoint target). Re-verify against upstream on every Aspire
  upgrade — this tracks Aspire internals.
