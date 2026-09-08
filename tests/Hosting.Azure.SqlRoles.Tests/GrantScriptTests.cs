using System.Diagnostics;
using Aspire.Hosting;
using Xunit;

namespace Hosting.Azure.SqlRoles.Tests;

/// <summary>
/// Runs the generated grant script inside the same container image Azure uses for deployment
/// scripts. Three separate faults reached production because they only appear in that image:
/// Invoke-Sqlcmd registering Always Encrypted providers, a parameter that does not exist on
/// Get-AzAccessToken, and Add-Type loading the Windows build of Microsoft.Data.SqlClient.
/// </summary>
public class GrantScriptTests
{
    // Reaching Open() means every earlier line worked. The host does not resolve, so a transport
    // error is the success signal; no Azure credentials are involved.
    private const string ReachedConnection = "TCP Provider";

    private static readonly string[] NeverAcceptable =
    [
        "ParameterBindingException",
        "MissingMethodException",
        "TypeInitializationException",
        "PlatformNotSupportedException",
        "CommandNotFoundException",
    ];

    [Fact]
    public async Task GrantScript_ReachesTheSqlConnection()
    {
        Assert.SkipUnless(await DockerIsAvailableAsync(), "Docker is required for this test.");

        var script = TestScript(AzureSqlDatabaseRoleExtensions.BuildScript(
            [SqlDatabaseRole.DbDataReader, SqlDatabaseRole.DbDataWriter]));

        var path = Path.Combine(Path.GetTempPath(), $"grant-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(path, script, TestContext.Current.CancellationToken);

        try
        {
            var (_, output) = await RunAsync("docker",
                $"""
                 run --rm --platform linux/amd64
                 -e DBSERVER=unreachable.database.windows.net -e DBNAME=TestDb
                 -e PRINCIPALNAME=test-identity -e ID=11111111-2222-3333-4444-555555555555
                 -v {path}:/tmp/grant.ps1:ro
                 mcr.microsoft.com/azuredeploymentscripts-powershell:az{AzureSqlDatabaseRoleExtensions.AzPowerShellVersion}
                 pwsh -NoProfile -File /tmp/grant.ps1
                 """.ReplaceLineEndings(" "),
                TimeSpan.FromMinutes(15));

            foreach (var fault in NeverAcceptable)
            {
                Assert.DoesNotContain(fault, output, StringComparison.Ordinal);
            }

            Assert.Contains("ALTER ROLE db_datareader", output, StringComparison.Ordinal);
            Assert.Contains("ALTER ROLE db_datawriter", output, StringComparison.Ordinal);
            Assert.Contains(ReachedConnection, output, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Get-AzAccessToken needs a signed-in Azure context, so it is replaced with a stub that returns
    // a SecureString exactly as Az.Accounts 5.x does. That keeps the unwrap branch under test.
    // The retry delay drops so five failed attempts take seconds rather than five minutes.
    private static string TestScript(string generated) =>
        """
        function Get-AzAccessToken {
            param($ResourceUrl)
            [pscustomobject]@{ Token = ConvertTo-SecureString "stub-token" -AsPlainText -Force }
        }

        """ + generated.Replace("$retryDelay = 60", "$retryDelay = 2");

    // The docker CLI exits 0 with a diagnostic on stdout when the daemon is unreachable, so the
    // exit code alone is not enough to tell a stopped daemon from a running one.
    private static async Task<bool> DockerIsAvailableAsync()
    {
        try
        {
            var (exitCode, output) = await RunAsync("docker", "version --format {{.Server.Version}}", TimeSpan.FromSeconds(20));
            return exitCode == 0 && !output.Contains("failed to connect", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string file, string arguments, TimeSpan timeout)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(file, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };

        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(timeout);
        await process.WaitForExitAsync(cts.Token);

        return (process.ExitCode, await stdout + await stderr);
    }
}
