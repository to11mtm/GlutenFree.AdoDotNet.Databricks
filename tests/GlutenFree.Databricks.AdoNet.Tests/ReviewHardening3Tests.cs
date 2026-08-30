using Apache.Arrow;
using GlutenFree.Databricks.AdoNet.Transport;

namespace GlutenFree.Databricks.AdoNet.Tests;

/// <summary>
/// Tests for the third round of PR-review fixes: negative sub-second interval formatting,
/// decimal parameter precision (leading zeros, full-scale fractions), and non-idempotent
/// submission retry policy (the latter is covered in <see cref="RestStatementTransportTests"/>).
/// </summary>
public class ReviewHardening3Tests
{
    [Theory]
    [InlineData(0, -100, "-0 00:00:00.100000000")] // sign must carry into the fraction
    [InlineData(0, 100, "0 00:00:00.100000000")]
    [InlineData(-1, -100, "-1 00:00:00.100000000")]
    [InlineData(5, 14_582_100, "5 04:03:02.100000000")]
    [InlineData(0, 0, "0 00:00:00.000000000")]
    public void DayTime_interval_formats_fraction_with_correct_sign(int days, int milliseconds, string expected)
    {
        var builder = new DayTimeIntervalArray.Builder();
        builder.Append(new Apache.Arrow.Scalars.DayTimeInterval(days, milliseconds));
        var array = builder.Build();

        var value = DatabricksTypeMap.ConvertArrowValue(
            array, 0, new ColumnInfo { Name = "i", TypeName = "INTERVAL" });

        Assert.Equal(expected, value);
    }

    [Fact]
    public void Decimal_parameter_excludes_cosmetic_leading_zero_from_precision()
    {
        // 0.0000000000000000000000000001m has scale 28 and one significant digit; counting
        // the leading "0" would overstate it as DECIMAL(29,28) and round-trip as SqlDecimal.
        var parameter = new DatabricksParameter("p", 0.0000000000000000000000000001m);

        var wire = parameter.ToStatementParameter();

        Assert.Equal("0.0000000000000000000000000001", wire.Value);
        Assert.Equal("DECIMAL(28,28)", wire.Type);
    }

    [Theory]
    [InlineData("0.5", "DECIMAL(1,1)")]
    [InlineData("123.450", "DECIMAL(6,3)")]
    [InlineData("0", "DECIMAL(1,0)")]
    [InlineData("-0.001", "DECIMAL(3,3)")]
    [InlineData("100", "DECIMAL(3,0)")]
    public void Decimal_parameter_precision_covers_unscaled_digits_and_scale(string text, string expectedType)
    {
        var parameter = new DatabricksParameter("p", decimal.Parse(
            text, System.Globalization.CultureInfo.InvariantCulture));

        var wire = parameter.ToStatementParameter();

        Assert.Equal(text, wire.Value);
        Assert.Equal(expectedType, wire.Type);
    }
}
