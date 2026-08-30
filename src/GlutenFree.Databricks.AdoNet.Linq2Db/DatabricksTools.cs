using GlutenFree.Databricks.AdoNet;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider;
using GlutenFree.Databricks.AdoNet.Linq2Db.Internal;

namespace GlutenFree.Databricks.AdoNet.Linq2Db;

/// <summary>
/// Entry points for using linq2db with Databricks SQL warehouses.
/// </summary>
/// <example>
/// <code>
/// using var db = DatabricksTools.CreateDataConnection(
///     "Host=https://adb-123.azuredatabricks.net;WarehouseId=abc;Token=dapi...");
/// var rows = db.GetTable&lt;Order&gt;().Where(o =&gt; o.Amount &gt; 100).ToList();
/// </code>
/// </example>
public static class DatabricksTools
{
    private static readonly DatabricksDataProvider s_provider = new();

    /// <summary>The singleton Databricks data provider instance.</summary>
    public static IDataProvider GetDataProvider() => s_provider;

    /// <summary>Creates a <see cref="DataConnection"/> from a GlutenFree.Databricks.AdoNet connection string.</summary>
    public static DataConnection CreateDataConnection(string connectionString)
        => new(new DataOptions().UseConnectionString(s_provider, connectionString));

    /// <summary>Creates a <see cref="DataConnection"/> over an existing (open or closed) connection.</summary>
    public static DataConnection CreateDataConnection(DatabricksConnection connection)
        => new(new DataOptions().UseConnection(s_provider, connection));

    /// <summary>Adds the Databricks provider to a <see cref="DataOptions"/> pipeline.</summary>
    public static DataOptions UseDatabricks(this DataOptions options, string connectionString)
        => options.UseConnectionString(s_provider, connectionString);

    /// <summary>Adds the Databricks provider with an existing connection to a <see cref="DataOptions"/> pipeline.</summary>
    public static DataOptions UseDatabricks(this DataOptions options, DatabricksConnection connection)
        => options.UseConnection(s_provider, connection);
}
