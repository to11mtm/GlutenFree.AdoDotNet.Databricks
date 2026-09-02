using System.Data.Common;
using GlutenFree.Databricks.AdoNet;
using GlutenFree.EntityFrameworkCore.Databricks.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Storage;

namespace GlutenFree.EntityFrameworkCore.Databricks.Storage.Internal;

/// <summary>
/// The Databricks <see cref="RelationalConnection" />, backed by
/// <see cref="DatabricksConnection" />.
/// </summary>
/// <remarks>
/// Transactions are left to the base implementation: whether one can be started depends on the
/// transport behind the connection (the session-based Thrift transport supports interactive
/// transactions; the stateless REST transport throws <see cref="NotSupportedException" />).
/// </remarks>
public class DatabricksRelationalConnection : RelationalConnection, IDatabricksRelationalConnection
{
    private readonly string? _catalog;
    private readonly string? _schema;

    /// <summary>Creates the connection.</summary>
    public DatabricksRelationalConnection(RelationalConnectionDependencies dependencies)
        : base(dependencies)
    {
        var extension = dependencies.ContextOptions.Extensions.OfType<DatabricksOptionsExtension>().FirstOrDefault();
        _catalog = extension?.Catalog;
        _schema = extension?.Schema;
    }

    /// <inheritdoc />
    protected override DbConnection CreateDbConnection()
        => new DatabricksConnection(ApplyNamespaceOverrides(GetValidatedConnectionString()));

    /// <summary>
    /// Applies the catalog/schema configured through <c>UseDatabricks</c> by overriding the
    /// corresponding connection-string keywords. <see cref="DatabricksConnection.ChangeCatalog" />
    /// and <see cref="System.Data.Common.DbConnection.ChangeDatabase" /> require an open
    /// connection, so the override has to happen before the connection is constructed.
    /// </summary>
    private string ApplyNamespaceOverrides(string connectionString)
    {
        if (_catalog is not { Length: > 0 } && _schema is not { Length: > 0 })
        {
            return connectionString;
        }

        var builder = new DatabricksConnectionStringBuilder(connectionString);

        if (_catalog is { Length: > 0 })
        {
            builder.Catalog = _catalog;
        }

        if (_schema is { Length: > 0 })
        {
            builder.Schema = _schema;
        }

        return builder.ConnectionString;
    }
}
