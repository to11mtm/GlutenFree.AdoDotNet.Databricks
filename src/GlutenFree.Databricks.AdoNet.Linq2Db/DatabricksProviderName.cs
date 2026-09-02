namespace GlutenFree.Databricks.AdoNet.Linq2Db;

/// <summary>Provider name constants for the Databricks linq2db provider.</summary>
public static class DatabricksProviderName
{
    /// <summary>The configuration/provider name registered with linq2db.</summary>
    public const string Databricks = "Databricks";

    /// <summary>
    /// The configuration/provider name for the Thrift (session-based) transport flavor,
    /// provided by the GlutenFree.Databricks.AdoNet.Linq2Db.Thrift package. Distinct from
    /// <see cref="Databricks"/> because linq2db keys configurations and options caching by
    /// provider name, and the two flavors differ in transaction support.
    /// </summary>
    public const string DatabricksThrift = "Databricks.Thrift";
}
