using System.Reflection;
using GlutenFree.Databricks.AdoNet.Auth;
using GlutenFree.Databricks.AdoNet.Transport;

namespace GlutenFree.Databricks.AdoNet.Tests;

/// <summary>
/// Verifies process-wide HTTP resource sharing: one HttpClient/handler across connections,
/// shared OAuth authenticators per credential set, and no disposal of shared resources.
/// </summary>
public class SharedResourcesTests
{
    private static HttpClient GetTransportClient(RestStatementTransport transport)
        => (HttpClient)typeof(RestStatementTransport)
            .GetField("_httpClient", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(transport)!;

    [Fact]
    public void Transports_without_injected_client_share_the_process_client()
    {
        using var transport1 = new RestStatementTransport("https://adb-1.example.net", new PatAuthenticator("t"));
        using var transport2 = new RestStatementTransport("https://adb-2.example.net", new PatAuthenticator("t"));

        Assert.Same(DatabricksSharedResources.HttpClient, GetTransportClient(transport1));
        Assert.Same(GetTransportClient(transport1), GetTransportClient(transport2));
    }

    [Fact]
    public async Task Disposing_a_transport_does_not_kill_the_shared_client()
    {
        var transport = new RestStatementTransport("https://adb-1.example.net", new PatAuthenticator("t"));
        await transport.DisposeAsync();

        // A fresh transport must get a still-usable shared client (disposed HttpClient
        // instances throw ObjectDisposedException on any use, including CancelPendingRequests).
        DatabricksSharedResources.HttpClient.CancelPendingRequests();
        using var next = new RestStatementTransport("https://adb-1.example.net", new PatAuthenticator("t"));
        Assert.Same(DatabricksSharedResources.HttpClient, GetTransportClient(next));
    }

    [Fact]
    public void OAuth_authenticators_are_shared_per_credential_set()
    {
        var a1 = DatabricksSharedResources.GetOAuthAuthenticator("https://adb-1.example.net", "id", "secret");
        var a2 = DatabricksSharedResources.GetOAuthAuthenticator("https://adb-1.example.net", "id", "secret");
        var different = DatabricksSharedResources.GetOAuthAuthenticator("https://adb-1.example.net", "id2", "secret");

        Assert.Same(a1, a2);
        Assert.NotSame(a1, different);
    }

    [Fact]
    public async Task Reopening_a_connection_does_not_dispose_shared_state()
    {
        var (connection, _) = DatabricksConnectionTests.CreateOpenable();
        await connection.OpenAsync();
        await connection.CloseAsync();
        await connection.OpenAsync();
        await connection.CloseAsync();

        DatabricksSharedResources.HttpClient.CancelPendingRequests(); // still alive
    }
}
