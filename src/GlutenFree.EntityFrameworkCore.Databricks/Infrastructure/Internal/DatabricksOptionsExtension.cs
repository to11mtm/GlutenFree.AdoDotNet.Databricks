using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace GlutenFree.EntityFrameworkCore.Databricks.Infrastructure.Internal;

/// <summary>
/// The <see cref="RelationalOptionsExtension" /> that identifies Databricks as the configured
/// provider and carries its provider-specific options.
/// </summary>
public class DatabricksOptionsExtension : RelationalOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;
    private string? _catalog;
    private string? _schema;

    /// <summary>Creates an extension with no options set.</summary>
    public DatabricksOptionsExtension()
    {
    }

    // NB: keep the copy constructor in sync when adding options.

    /// <summary>Copy constructor used by <see cref="Clone" />.</summary>
    protected DatabricksOptionsExtension(DatabricksOptionsExtension copyFrom)
        : base(copyFrom)
    {
        _catalog = copyFrom._catalog;
        _schema = copyFrom._schema;
    }

    /// <inheritdoc />
    public override DbContextOptionsExtensionInfo Info
        => _info ??= new ExtensionInfo(this);

    /// <summary>
    /// The Unity Catalog catalog to resolve unqualified names against, overriding the
    /// connection string's <c>Catalog</c> keyword.
    /// </summary>
    public virtual string? Catalog
        => _catalog;

    /// <summary>
    /// The default schema to resolve unqualified names against, overriding the connection
    /// string's <c>Schema</c> keyword.
    /// </summary>
    public virtual string? Schema
        => _schema;

    /// <summary>Returns a copy with <see cref="Catalog" /> set.</summary>
    public virtual DatabricksOptionsExtension WithCatalog(string? catalog)
    {
        var clone = (DatabricksOptionsExtension)Clone();
        clone._catalog = catalog;
        return clone;
    }

    /// <summary>Returns a copy with <see cref="Schema" /> set.</summary>
    public virtual DatabricksOptionsExtension WithSchema(string? schema)
    {
        var clone = (DatabricksOptionsExtension)Clone();
        clone._schema = schema;
        return clone;
    }

    /// <inheritdoc />
    protected override RelationalOptionsExtension Clone()
        => new DatabricksOptionsExtension(this);

    /// <inheritdoc />
    public override void ApplyServices(IServiceCollection services)
        => services.AddEntityFrameworkDatabricks();

    private sealed class ExtensionInfo(IDbContextOptionsExtension extension) : RelationalExtensionInfo(extension)
    {
        private string? _logFragment;

        private new DatabricksOptionsExtension Extension
            => (DatabricksOptionsExtension)base.Extension;

        public override bool IsDatabaseProvider
            => true;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
            => other is ExtensionInfo otherInfo
                && Extension._catalog == otherInfo.Extension._catalog
                && Extension._schema == otherInfo.Extension._schema;

        public override string LogFragment
        {
            get
            {
                if (_logFragment is null)
                {
                    var builder = new System.Text.StringBuilder();
                    builder.Append(base.LogFragment);

                    if (Extension._catalog is not null)
                    {
                        builder.Append("Catalog=").Append(Extension._catalog).Append(' ');
                    }

                    if (Extension._schema is not null)
                    {
                        builder.Append("Schema=").Append(Extension._schema).Append(' ');
                    }

                    _logFragment = builder.ToString();
                }

                return _logFragment;
            }
        }

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
            => debugInfo["Databricks"] = "1";
    }
}
