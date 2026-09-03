using GlutenFree.Databricks.AdoNet;
using GlutenFree.Databricks.AdoNet.IntegrationTests;
using Microsoft.EntityFrameworkCore;

namespace GlutenFree.EntityFrameworkCore.Databricks.IntegrationTests;

/// <summary>
/// Live coverage for <c>DECIMAL</c> columns wider than a .NET <see cref="decimal" />, which the
/// provider maps to the arbitrary-precision <see cref="DatabricksDecimal" />.
/// </summary>
[Trait("Category", "Integration")]
public class EfCoreBigDecimalIntegrationTests : WidgetFixture
{
    [IntegrationFact]
    public async Task Wide_decimal_columns_materialize_without_loss()
    {
        await using var context = CreateContext();

        var values = await Widgets(context)
            .OrderBy(w => w.Id)
            .Select(w => w.BigValue)
            .ToListAsync();

        // 38 significant digits: reading this into a .NET decimal would overflow.
        Assert.Equal("1234567890123456789012345678.1234567890", values[0].ToString());
        Assert.Equal("0.0000000001", values[1].ToString());
        Assert.Equal("-9999999999999999999999999999.9999999999", values[2].ToString());
    }

    [IntegrationFact]
    public async Task Wide_decimal_precision_and_scale_survive()
    {
        await using var context = CreateContext();

        var value = await Widgets(context).Where(w => w.Id == 1).Select(w => w.BigValue).SingleAsync();

        Assert.Equal(38, value.Precision);
        Assert.Equal(10, value.Scale);
    }

    [IntegrationFact]
    public async Task Wide_decimals_can_be_compared_server_side()
    {
        await using var context = CreateContext();
        var threshold = DatabricksDecimal.Parse("1.0");

        var ids = await Widgets(context)
            .Where(w => w.BigValue > threshold)
            .Select(w => w.Id)
            .ToListAsync();

        Assert.Equal([1L], ids);
    }

    [IntegrationFact]
    public async Task Wide_decimals_can_be_ordered_server_side()
    {
        await using var context = CreateContext();

        var ids = await Widgets(context)
            .OrderBy(w => w.BigValue)
            .Select(w => w.Id)
            .ToListAsync();

        Assert.Equal([3L, 2L, 1L], ids);
    }

    [IntegrationFact]
    public async Task Wide_decimals_round_trip_through_a_parameter()
    {
        await using var context = CreateContext();
        var exact = DatabricksDecimal.Parse("1234567890123456789012345678.1234567890");

        var found = await Widgets(context).Where(w => w.BigValue == exact).Select(w => w.Id).SingleAsync();

        Assert.Equal(1L, found);
    }
}
