using System.Data.SqlTypes;
using GlutenFree.Databricks.AdoNet.Linq2Db.Internal;

namespace GlutenFree.Databricks.AdoNet.Linq2Db.Tests;

public class DatabricksSchemaProviderTypeTests
{
    [Theory]
    [InlineData("decimal", "decimal(10,2)", typeof(decimal))]
    [InlineData("decimal", "decimal(28,6)", typeof(decimal))]
    [InlineData("decimal", "decimal(29,0)", typeof(SqlDecimal))]
    [InlineData("decimal", "decimal(38,0)", typeof(SqlDecimal))]
    [InlineData("decimal", "decimal(38,18)", typeof(SqlDecimal))]
    [InlineData("decimal", null, typeof(decimal))] // Databricks default is DECIMAL(10,0)
    [InlineData("decimal", "decimal", typeof(decimal))]
    [InlineData("bigint", null, typeof(long))]
    [InlineData("string", null, typeof(string))]
    public void Scaffolded_type_matches_reader_precision_mapping(
        string dataType, string? fullDataType, Type expected)
    {
        Assert.Equal(expected, DatabricksSchemaProvider.GetSystemType(dataType, fullDataType));
    }

    [Theory]
    [InlineData("decimal(38,2)", 38)]
    [InlineData("decimal(10,0)", 10)]
    [InlineData("decimal( 12 ,2)", 12)]
    [InlineData("decimal", 10)]
    [InlineData(null, 10)]
    [InlineData("decimal(oops)", 10)]
    public void Decimal_precision_parses_from_full_type_text(string? fullDataType, int expected)
    {
        Assert.Equal(expected, DatabricksSchemaProvider.GetDecimalPrecision(fullDataType));
    }
}
