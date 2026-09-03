using Microsoft.EntityFrameworkCore.Storage;

namespace GlutenFree.EntityFrameworkCore.Databricks.Storage.Internal;

/// <summary>Marker interface for the provider-specific relational connection.</summary>
public interface IDatabricksRelationalConnection : IRelationalConnection
{
}
