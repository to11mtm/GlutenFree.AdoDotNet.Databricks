using System.Data.Common;

namespace Databricks.AdoNet;

/// <summary>
/// <see cref="DbProviderFactory"/> for the Databricks ADO.NET provider.
/// Register with <c>DbProviderFactories.RegisterFactory("Databricks.AdoNet", DatabricksProviderFactory.Instance)</c>.
/// </summary>
public sealed class DatabricksProviderFactory : DbProviderFactory
{
    /// <summary>The singleton factory instance.</summary>
    public static readonly DatabricksProviderFactory Instance = new();

    private DatabricksProviderFactory()
    {
    }

    /// <inheritdoc />
    public override bool CanCreateDataAdapter => false;

    /// <inheritdoc />
    public override bool CanCreateCommandBuilder => false;

    /// <inheritdoc />
    public override DbCommand CreateCommand() => new DatabricksCommand();

    /// <inheritdoc />
    public override DbConnection CreateConnection() => new DatabricksConnection();

    /// <inheritdoc />
    public override DbConnectionStringBuilder CreateConnectionStringBuilder()
        => new DatabricksConnectionStringBuilder();

    /// <inheritdoc />
    public override DbParameter CreateParameter() => new DatabricksParameter();
}
