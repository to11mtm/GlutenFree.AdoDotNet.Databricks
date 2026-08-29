using GlutenFree.Databricks.AdoNet;
using GlutenFree.Databricks.AdoNet.Tests;
using GlutenFree.Databricks.AdoNet.Transport;

namespace GlutenFree.Databricks.AdoNet.DecimalTests;

/// <summary>
/// Verifies DatabricksDecimal integration with the provider surface:
/// reader accessors and parameter binding.
/// </summary>
public class DatabricksDecimalProviderIntegrationTests
{
    private const string Digits38 = "99999999999999999999999999999999999999";

    private static DatabricksDataReader CreateReader(int precision, int scale, string cellValue)
        => new(new FakeTransport(), new StatementResponse
        {
            StatementId = "stmt-1",
            Status = new StatementStatus { State = "SUCCEEDED" },
            Manifest = new ResultManifest
            {
                Format = "JSON_ARRAY",
                TotalChunkCount = 1,
                TotalRowCount = 1,
                Schema = new ResultSchema
                {
                    ColumnCount = 1,
                    Columns =
                    [
                        new ColumnInfo
                        {
                            Name = "d",
                            TypeName = "DECIMAL",
                            TypePrecision = precision,
                            TypeScale = scale,
                            Position = 0,
                        },
                    ],
                },
            },
            Result = new ResultData { ChunkIndex = 0, RowCount = 1, DataArray = [[cellValue]] },
        });

    [Fact]
    public async Task Reader_GetDatabricksDecimal_handles_high_precision_column()
    {
        var reader = CreateReader(38, 0, Digits38);
        Assert.True(await reader.ReadAsync());

        var value = reader.GetDatabricksDecimal(0);

        Assert.Equal(Digits38, value.ToString());
        Assert.Equal(38, value.Precision);
    }

    [Fact]
    public async Task Reader_GetDatabricksDecimal_handles_regular_decimal_column()
    {
        var reader = CreateReader(10, 2, "1234.56");
        Assert.True(await reader.ReadAsync());

        Assert.Equal(DatabricksDecimal.Parse("1234.56"), reader.GetDatabricksDecimal(0));
    }

    [Fact]
    public async Task Reader_GetFieldValue_supports_DatabricksDecimal()
    {
        var reader = CreateReader(38, 6, "12345678901234567890123456789012.345678");
        Assert.True(await reader.ReadAsync());

        var value = reader.GetFieldValue<DatabricksDecimal>(0);

        Assert.Equal("12345678901234567890123456789012.345678", value.ToString());
    }

    [Fact]
    public void Parameter_infers_decimal_wire_type_from_DatabricksDecimal()
    {
        var value = DatabricksDecimal.Parse("12345678901234567890123456789012345.678");
        var parameter = new DatabricksParameter("p", value);

        var wire = parameter.ToStatementParameter();

        Assert.Equal("12345678901234567890123456789012345.678", wire.Value);
        Assert.Equal("DECIMAL(38,3)", wire.Type);
    }

    [Fact]
    public void Parameter_handles_small_fraction_precision()
    {
        // 0.005 has 1 significant digit but needs precision >= scale + 1.
        var parameter = new DatabricksParameter("p", DatabricksDecimal.Parse("0.005"));

        var wire = parameter.ToStatementParameter();

        Assert.Equal("0.005", wire.Value);
        Assert.Equal("DECIMAL(4,3)", wire.Type);
    }
}
