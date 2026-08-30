using GlutenFree.Databricks.AdoNet.IntegrationTests;

namespace GlutenFree.Databricks.AdoNet.Thrift.IntegrationTests;

/// <summary>
/// Session semantics that only hold on the Thrift transport: catalog/schema context is
/// real server-side session state (the transport replays <c>USE</c> only on change),
/// so it persists across commands on one connection.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ThriftSessionIntegrationTests
{
    [IntegrationFact]
    public async Task Catalog_and_schema_persist_across_commands()
    {
        await using var connection = IntegrationConfig.CreateConnection(
            "Catalog=workspace;Schema=information_schema");
        await connection.OpenAsync();

        await using (var first = connection.CreateCommand())
        {
            first.CommandText = "SELECT current_catalog(), current_schema()";
            await using var reader = await first.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("workspace", reader.GetString(0));
            Assert.Equal("information_schema", reader.GetString(1));
        }

        // A second command on the same connection sees the same session context
        // without any USE replay (unqualified reference resolves in-session).
        await using (var second = connection.CreateCommand())
        {
            second.CommandText = "SELECT COUNT(*) FROM schemata WHERE schema_name = 'information_schema'";
            await using var reader = await second.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, reader.GetInt64(0));
        }
    }

    [IntegrationFact]
    public async Task ChangeDatabase_switches_session_schema()
    {
        await using var connection = IntegrationConfig.CreateConnection("Catalog=workspace");
        await connection.OpenAsync();

        connection.ChangeDatabase("information_schema");

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_schema()";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("information_schema", reader.GetString(0));
    }
}
