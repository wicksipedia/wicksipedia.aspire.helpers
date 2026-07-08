using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure.Provisioning;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Network;
using Azure.Provisioning.Primitives;
using Azure.Provisioning.Resources;
using Azure.Provisioning.Roles;
using Azure.Provisioning.Storage;

namespace Aspire.Hosting;

/// <summary>In-database (fixed) SQL roles that a managed identity can be granted.</summary>
public enum SqlDatabaseRole
{
    DbOwner,
    DbDataReader,
    DbDataWriter,
    DbDdlAdmin,
    DbSecurityAdmin,
    DbAccessAdmin,
}

/// <summary>
/// Grants a user-assigned managed identity specific DATABASE-level roles on an Azure SQL server,
/// instead of Aspire's built-in behaviour of granting <c>db_owner</c> to every referencing identity.
///
/// <para>Aspire's Azure SQL integration runs a deployment script that hardcodes
/// <c>ALTER ROLE db_owner ADD MEMBER</c> and ignores the requested roles. This extension disables
/// that (<c>ClearDefaultRoleAssignments</c>) and emits its own deployment scripts — one per identity
/// per database — that create the identity's user and add it to exactly the roles you ask for. The
/// scripts run as the SQL server's Entra admin identity (which Aspire provisions).</para>
///
/// <para>When the server has <b>public network access</b> the scripts run as-is. Once the server is put
/// behind a <b>private endpoint</b>, the scripts' container must run inside the VNet and reach a storage
/// file share; call <see cref="WithGrantScriptNetwork"/> with a subnet + storage account and this
/// extension wires up the storage file private endpoint, NSG, subnet delegation and identity role.</para>
///
/// <para>Depends only on public Aspire + Azure.Provisioning APIs. Publish-only — local
/// <c>RunAsContainer</c> uses SQL auth and is a no-op here.</para>
/// </summary>
public static class AzureSqlDatabaseRoleExtensions
{
    // ACI subnet delegation service name — the deployment-script container group runs here.
    private const string AciDelegation = "Microsoft.ContainerInstance/containerGroups";

    private static readonly ConditionalWeakTable<AzureSqlServerResource, GrantState> State = new();

    private sealed class GrantState
    {
        public readonly List<(IResourceBuilder<AzureUserAssignedIdentityResource> Identity, SqlDatabaseRole[] Roles)> Grants = [];

        // Private-endpoint plumbing, set by WithGrantScriptNetwork.
        public IResourceBuilder<AzureSubnetResource>? Subnet;
        public IResourceBuilder<AzureStorageResource>? Storage;
        public readonly List<AzureProvisioningResource> DependsOnPes = [];
    }

    /// <summary>Grants <paramref name="identity"/> the given <paramref name="roles"/> on every database of the server.</summary>
    public static IResourceBuilder<AzureSqlServerResource> WithSqlDatabaseRoles(
        this IResourceBuilder<AzureSqlServerResource> sql,
        IResourceBuilder<AzureUserAssignedIdentityResource> identity,
        params SqlDatabaseRole[] roles)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(identity);
        if (roles is null || roles.Length == 0)
        {
            throw new ArgumentException("At least one role is required.", nameof(roles));
        }

        // Roles are an Azure/publish concern; the local SQL container uses SA auth.
        if (!sql.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return sql;
        }

        GetOrCreateState(sql).Grants.Add((identity, roles));
        return sql;
    }

    /// <summary>
    /// Supplies the in-VNet plumbing the grant scripts need once the SQL server is behind a private
    /// endpoint: the <paramref name="subnet"/> the script container runs in (delegated to ACI here) and
    /// a <paramref name="storage"/> account for its file share. This extension adds the storage file
    /// private endpoint, an NSG (outbound 443 to Entra ID + SQL) and grants the SQL admin identity file
    /// access. No-op when the server has no private endpoint (public access) — the scripts run as-is.
    ///
    /// <para>Call this <b>after</b> <c>AddPrivateEndpoint(sql)</c>: the plumbing is wired eagerly against
    /// the private endpoint already in the model (doing it in a later event hits a read-only container).</para>
    /// </summary>
    public static IResourceBuilder<AzureSqlServerResource> WithGrantScriptNetwork(
        this IResourceBuilder<AzureSqlServerResource> sql,
        IResourceBuilder<AzureSubnetResource> subnet,
        IResourceBuilder<AzureStorageResource> storage)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(subnet);
        ArgumentNullException.ThrowIfNull(storage);

        if (!sql.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return sql;
        }

        var state = GetOrCreateState(sql);
        state.Subnet = subnet;
        state.Storage = storage;

        // The deployment-script ACI mounts the file share with the storage account key, so shared-key
        // access must be on (AddAzureStorage defaults it off). Without this the ACI never provisions.
        storage.ConfigureInfrastructure(infra =>
            infra.GetProvisionableResources().OfType<StorageAccount>().Single().AllowSharedKeyAccess = true);

        ConfigurePrivateEndpointPlumbing(sql, state);
        return sql;
    }

    private static GrantState GetOrCreateState(IResourceBuilder<AzureSqlServerResource> sql)
    {
        if (State.TryGetValue(sql.Resource, out var state))
        {
            return state;
        }

        state = new GrantState();
        State.Add(sql.Resource, state);

        // Stop Aspire's built-in script from granting db_owner to every referencing identity.
        sql.ClearDefaultRoleAssignments();

        // One dedicated module holds all the grant scripts; it reads `state` at publish time
        // (after every WithSqlDatabaseRoles/WithGrantScriptNetwork call and every AddDatabase have run).
        sql.ApplicationBuilder.AddAzureInfrastructure(
            $"{sql.Resource.Name}-roles",
            infra => EmitGrantScripts(infra, sql.Resource, state));

        return state;
    }

    // Builds the private-endpoint plumbing, only when a private endpoint actually targets the SQL
    // server — otherwise the scripts run against public SQL and need nothing.
    private static void ConfigurePrivateEndpointPlumbing(
        IResourceBuilder<AzureSqlServerResource> sql, GrantState state)
    {
        if (state.Subnet is null || state.Storage is null || state.DependsOnPes.Count > 0)
        {
            return; // no network supplied, or already plumbed.
        }

        var pe = sql.ApplicationBuilder.Resources.OfType<AzurePrivateEndpointResource>()
            .FirstOrDefault(p => ReferenceEquals(p.Target, sql.Resource));
        if (pe is null)
        {
            return; // public network access → nothing to plumb.
        }

        var builder = sql.ApplicationBuilder;

        // 1. A private endpoint to the storage account's file service, on the same subnet as the SQL PE,
        //    so the script container can mount the file share privately.
        var peSubnet = builder.CreateResourceBuilder(pe.Subnet);
        var storagePe = peSubnet.AddPrivateEndpoint(builder.CreateResourceBuilder(new StorageFiles(state.Storage.Resource)));

        // Scripts must wait for both private endpoints before they can reach SQL / storage.
        state.DependsOnPes.Add(pe);
        state.DependsOnPes.Add(storagePe.Resource);

        // 2. Delegate the ACI subnet to container groups and lock its egress to Entra ID + SQL.
        state.Subnet.Resource.Annotations.Add(new AzureSubnetServiceDelegationAnnotation(AciDelegation, AciDelegation));
        var nsg = builder.AddNetworkSecurityGroup($"{sql.Resource.Name}-aci-nsg")
            .WithSecurityRule(OutboundHttpsRule("allow-outbound-443-aad", 100, AzureServiceTags.AzureActiveDirectory))
            .WithSecurityRule(OutboundHttpsRule("allow-outbound-443-sql", 200, AzureServiceTags.Sql));
        state.Subnet.WithNetworkSecurityGroup(nsg);

        // 3. Let the SQL admin identity (which the scripts run as) mount the storage file share —
        //    identity-based, so the deployment script needs only the storage account name, no key.
        builder.AddAzureUserAssignedIdentity($"{sql.Resource.Name}-admin-id")
            .WithAnnotation(new ExistingAzureResourceAnnotation(new BicepOutputReference("sqlServerAdminName", sql.Resource)))
            .WithRoleAssignments(state.Storage, StorageBuiltInRole.StorageFileDataPrivilegedContributor);
    }

    private static AzureSecurityRule OutboundHttpsRule(string name, int priority, string destinationServiceTag) => new()
    {
        Name = name,
        Priority = priority,
        Direction = SecurityRuleDirection.Outbound,
        Access = SecurityRuleAccess.Allow,
        Protocol = SecurityRuleProtocol.Tcp,
        SourceAddressPrefix = "*",
        SourcePortRange = "*",
        DestinationAddressPrefix = destinationServiceTag,
        DestinationPortRange = "443",
    };

    private static void EmitGrantScripts(AzureResourceInfrastructure infra, AzureSqlServerResource sql, GrantState state)
    {
        // Run the scripts as the SQL server's Entra admin identity (Aspire auto-creates + outputs it).
        var admin = UserAssignedIdentity.FromExisting("sqlServerAdmin");
        admin.Name = new BicepOutputReference("sqlServerAdminName", sql).AsProvisioningParameter(infra);
        infra.Add(admin);
        var adminId = BicepFunction.Interpolate($"{admin.Id}").Compile().ToString();

        var fqdn = new BicepOutputReference("sqlServerFqdn", sql).AsProvisioningParameter(infra);

        // If a network was supplied AND a private endpoint exists (DependsOnPes populated by
        // WithGrantScriptNetwork), run every script in the VNet subnet against the storage file share.
        var isPrivate = state.Subnet is not null && state.Storage is not null && state.DependsOnPes.Count > 0;
        ProvisioningParameter? subnetIdParam = null;
        BicepValue<string>? storageAccountName = null;
        var dependsOn = new List<ProvisionableResource>();
        if (isPrivate)
        {
            subnetIdParam = state.Subnet!.Resource.Id.AsProvisioningParameter(infra, "grantScriptSubnetId");
            storageAccountName = ((StorageAccount)state.Storage!.Resource.AddAsExistingResource(infra)).Name;
            foreach (var pe in state.DependsOnPes)
            {
                dependsOn.Add(pe.AddAsExistingResource(infra));
            }
        }

        foreach (var (identityBuilder, roles) in state.Grants)
        {
            var id = identityBuilder.Resource;
            var principalName = id.PrincipalName.AsProvisioningParameter(infra, $"{id.GetBicepIdentifier()}_name");
            var clientId = id.ClientId.AsProvisioningParameter(infra, $"{id.GetBicepIdentifier()}_clientId");
            var script = BuildScript(roles);

            foreach (var (dbResourceName, dbName) in sql.Databases)
            {
                var resource = new AzurePowerShellScript(
                    $"grant_{Infrastructure.NormalizeBicepIdentifier($"{id.Name}_{dbResourceName}")}")
                {
                    Name = BicepFunction.Take(
                        BicepFunction.Interpolate($"grant-{BicepFunction.GetUniqueString(id.GetBicepIdentifier(), new StringLiteralExpression(dbResourceName), BicepFunction.GetResourceGroup().Id)}"),
                        24),
                    RetentionInterval = TimeSpan.FromHours(1),
                    AzPowerShellVersion = "14.0",
                };
                resource.Identity.IdentityType = ArmDeploymentScriptManagedIdentityType.UserAssigned;
                resource.Identity.UserAssignedIdentities[adminId] = new UserAssignedIdentityDetails();
                resource.EnvironmentVariables.Add(new ScriptEnvironmentVariable { Name = "DBNAME", Value = dbName });
                resource.EnvironmentVariables.Add(new ScriptEnvironmentVariable { Name = "DBSERVER", Value = fqdn });
                resource.EnvironmentVariables.Add(new ScriptEnvironmentVariable { Name = "PRINCIPALNAME", Value = principalName });
                resource.EnvironmentVariables.Add(new ScriptEnvironmentVariable { Name = "ID", Value = clientId });
                resource.ScriptContent = script;

                if (isPrivate)
                {
                    resource.ContainerSettings.SubnetIds.Add(new ScriptContainerGroupSubnet { Id = subnetIdParam });
                    resource.StorageAccountSettings.StorageAccountName = storageAccountName;
                    foreach (var d in dependsOn)
                    {
                        resource.DependsOn.Add(d);
                    }
                }

                infra.Add(resource);
            }
        }
    }

    private static string RoleName(SqlDatabaseRole role) => role switch
    {
        SqlDatabaseRole.DbOwner => "db_owner",
        SqlDatabaseRole.DbDataReader => "db_datareader",
        SqlDatabaseRole.DbDataWriter => "db_datawriter",
        SqlDatabaseRole.DbDdlAdmin => "db_ddladmin",
        SqlDatabaseRole.DbSecurityAdmin => "db_securityadmin",
        SqlDatabaseRole.DbAccessAdmin => "db_accessadmin",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    // A storage "file" sub-resource as a private-endpoint target (Aspire keeps the equivalent internal).
    private sealed class StorageFiles(AzureStorageResource storage) : Resource("files"), IResourceWithParent, IAzurePrivateEndpointTarget
    {
        public BicepOutputReference Id => storage.Id;

        public IResource Parent => storage;

        public IEnumerable<string> GetPrivateDnsZoneNames() => ["privatelink.file.core.windows.net"];

        public IEnumerable<string> GetPrivateLinkGroupIds()
        {
            yield return "file";
        }
    }

    // PowerShell (runs on ACI as the SQL admin) that creates the identity's user and adds it to the
    // requested roles — idempotent, so re-deploys are safe. Adapted from Aspire's own admin script.
    private static string BuildScript(SqlDatabaseRole[] roles)
    {
        var roleLines = string.Join(
            "\n            ",
            roles.Select(r => $"IF IS_ROLEMEMBER('{RoleName(r)}', @name) = 0 EXEC (N'ALTER ROLE {RoleName(r)} ADD MEMBER [' + @name + ']');"));

        return $$"""
            $sqlServerFqdn = "$env:DBSERVER"
            $sqlDatabaseName = "$env:DBNAME"
            $principalName = "$env:PRINCIPALNAME"
            $id = "$env:ID"

            # Pinned version avoids breaking changes in 22.4.5.1 (aspire#9926).
            Install-Module -Name SqlServer -RequiredVersion 22.3.0 -Force -AllowClobber -Scope CurrentUser
            Import-Module SqlServer

            $sqlCmd = @"
            DECLARE @name SYSNAME = '$principalName';
            DECLARE @id UNIQUEIDENTIFIER = '$id';
            DECLARE @castId NVARCHAR(MAX) = CONVERT(VARCHAR(MAX), CONVERT (VARBINARY(16), @id), 1);
            IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @name)
                EXEC (N'CREATE USER [' + @name + '] WITH SID = ' + @castId + ', TYPE = E;');
            {{roleLines}}
            "@
            # Note: the here-string terminator must not have leading whitespace.

            Write-Host $sqlCmd

            $connectionString = "Server=tcp:${sqlServerFqdn},1433;Initial Catalog=${sqlDatabaseName};Authentication=Active Directory Default;"

            $maxRetries = 5
            $retryDelay = 60
            $attempt = 0
            $success = $false

            while (-not $success -and $attempt -lt $maxRetries) {
                $attempt++
                Write-Host "Attempt $attempt of $maxRetries..."
                try {
                    Invoke-Sqlcmd -ConnectionString $connectionString -Query $sqlCmd
                    $success = $true
                    Write-Host "SQL command succeeded on attempt $attempt."
                } catch {
                    Write-Host "Attempt $attempt failed: $_"
                    if ($attempt -lt $maxRetries) {
                        Write-Host "Retrying in $retryDelay seconds..."
                        Start-Sleep -Seconds $retryDelay
                    } else {
                        throw
                    }
                }
            }
            """;
    }
}
