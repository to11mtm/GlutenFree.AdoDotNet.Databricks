using System.Data.SqlTypes;
using System.Numerics;
using GlutenFree.Databricks.AdoNet;

namespace GlutenFree.Databricks.AdoNet.DecimalTests;

public class DatabricksDecimalTests
{
    private const string Digits38 = "99999999999999999999999999999999999999";

    [Theory]
    [InlineData("0", "0")]
    [InlineData("1", "1")]
    [InlineData("-1", "-1")]
    [InlineData("12345.678", "12345.678")]
    [InlineData("-0.05", "-0.05")]
    [InlineData("0.500", "0.500")] // trailing zeros preserved
    [InlineData(".5", "0.5")]
    [InlineData("+7.25", "7.25")]
    [InlineData("007.10", "7.10")]
    public void Parse_and_ToString_roundtrip(string input, string expected)
    {
        Assert.Equal(expected, DatabricksDecimal.Parse(input).ToString());
    }

    [Fact]
    public void Parses_beyond_38_digits()
    {
        var value = DatabricksDecimal.Parse(Digits38 + ".123");

        Assert.Equal(41, value.Precision);
        Assert.Equal(3, value.Scale);
        Assert.Equal(Digits38 + ".123", value.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1.2.3")]
    [InlineData("1e5")]
    [InlineData(".")]
    [InlineData("-")]
    [InlineData("1,000")]
    public void TryParse_rejects_invalid_input(string input)
    {
        Assert.False(DatabricksDecimal.TryParse(input, out _));
        Assert.Throws<FormatException>(() => DatabricksDecimal.Parse(input));
    }

    [Fact]
    public void Small_fraction_renders_leading_zeros()
    {
        var value = new DatabricksDecimal(new BigInteger(5), 3);
        Assert.Equal("0.005", value.ToString());
    }

    [Fact]
    public void Negative_scale_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DatabricksDecimal(BigInteger.One, -1));
    }

    [Fact]
    public void Equality_is_numeric_across_scales()
    {
        var a = DatabricksDecimal.Parse("1.5");
        var b = DatabricksDecimal.Parse("1.50");

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, DatabricksDecimal.Parse("1.51"));
    }

    [Fact]
    public void Comparison_orders_numerically()
    {
        var values = new[] { "10", "-3.5", "0", "0.001", "-3.49", Digits38 }
            .Select(DatabricksDecimal.Parse)
            .OrderBy(v => v)
            .Select(v => v.ToString())
            .ToArray();

        Assert.Equal(["-3.5", "-3.49", "0", "0.001", "10", Digits38], values);
        Assert.True(DatabricksDecimal.Parse("2") > DatabricksDecimal.Parse("1.999"));
        Assert.True(DatabricksDecimal.Parse("-2") < DatabricksDecimal.Zero);
    }

    [Fact]
    public void Addition_and_subtraction_align_scales()
    {
        var result = DatabricksDecimal.Parse("1.5") + DatabricksDecimal.Parse("0.25");
        Assert.Equal("1.75", result.ToString());

        result = DatabricksDecimal.Parse("1") - DatabricksDecimal.Parse("0.001");
        Assert.Equal("0.999", result.ToString());

        // Arbitrary precision: no overflow at 38+ digits.
        result = DatabricksDecimal.Parse(Digits38) + DatabricksDecimal.One;
        Assert.Equal("100000000000000000000000000000000000000", result.ToString());
    }

    [Fact]
    public void Multiplication_adds_scales()
    {
        var result = DatabricksDecimal.Parse("1.5") * DatabricksDecimal.Parse("2.05");
        Assert.Equal("3.075", result.ToString());
        Assert.Equal(3, result.Scale);

        result = DatabricksDecimal.Parse("-0.5") * DatabricksDecimal.Parse("0.5");
        Assert.Equal("-0.25", result.ToString());
    }

    [Theory]
    [InlineData("1", "3", 4, MidpointRounding.ToEven, "0.3333")]
    [InlineData("2", "3", 4, MidpointRounding.ToEven, "0.6667")]
    [InlineData("1", "8", 2, MidpointRounding.ToEven, "0.12")] // 0.125 → even
    [InlineData("3", "8", 2, MidpointRounding.ToEven, "0.38")] // 0.375 → even
    [InlineData("1", "8", 2, MidpointRounding.AwayFromZero, "0.13")]
    [InlineData("-1", "8", 2, MidpointRounding.ToEven, "-0.12")]
    [InlineData("-1", "8", 2, MidpointRounding.AwayFromZero, "-0.13")]
    [InlineData("10", "4", 0, MidpointRounding.ToEven, "2")] // 2.5 → even
    [InlineData("10", "2", 3, MidpointRounding.ToEven, "5.000")]
    public void Division_rounds_correctly(
        string dividend, string divisor, int scale, MidpointRounding rounding, string expected)
    {
        var result = DatabricksDecimal.Parse(dividend)
            .Divide(DatabricksDecimal.Parse(divisor), scale, rounding);

        Assert.Equal(expected, result.ToString());
    }

    [Fact]
    public void Division_by_zero_throws()
    {
        Assert.Throws<DivideByZeroException>(
            () => DatabricksDecimal.One.Divide(DatabricksDecimal.Zero, 2));
    }

    [Fact]
    public void Decimal_conversions_are_lossless_in_range()
    {
        var value = DatabricksDecimal.FromDecimal(-1234.5678m);
        Assert.Equal("-1234.5678", value.ToString());
        Assert.Equal(-1234.5678m, value.ToDecimal());
        Assert.Equal(-1234.5678m, (decimal)value);

        DatabricksDecimal implicitConverted = 42.5m;
        Assert.Equal("42.5", implicitConverted.ToString());

        Assert.Equal(decimal.MaxValue, DatabricksDecimal.FromDecimal(decimal.MaxValue).ToDecimal());
        Assert.Equal(decimal.MinValue, DatabricksDecimal.FromDecimal(decimal.MinValue).ToDecimal());
    }

    [Fact]
    public void ToDecimal_overflows_loudly()
    {
        Assert.Throws<OverflowException>(() => DatabricksDecimal.Parse(Digits38).ToDecimal());
    }

    [Fact]
    public void SqlDecimal_conversions_are_lossless_to_38_digits()
    {
        var text = "12345678901234567890123456789012345.678";
        var value = DatabricksDecimal.FromSqlDecimal(SqlDecimal.Parse(text));

        Assert.Equal(text, value.ToString());
        Assert.Equal(SqlDecimal.Parse(text), value.ToSqlDecimal());
    }

    [Fact]
    public void ToSqlDecimal_beyond_38_digits_overflows_loudly()
    {
        var value = DatabricksDecimal.Parse(Digits38 + "9");
        Assert.ThrowsAny<Exception>(() => value.ToSqlDecimal());
    }

    [Fact]
    public void Normalize_strips_trailing_zeros()
    {
        Assert.Equal(1, DatabricksDecimal.Parse("1.500").Normalize().Scale);
        Assert.Equal(0, DatabricksDecimal.Parse("0.000").Normalize().Scale);
        Assert.Equal("1.5", DatabricksDecimal.Parse("1.500").Normalize().ToString());
    }

    [Fact]
    public void Sign_abs_and_properties_behave()
    {
        var negative = DatabricksDecimal.Parse("-12.34");

        Assert.Equal(-1, negative.Sign);
        Assert.Equal("12.34", negative.Abs().ToString());
        Assert.Equal("12.34", (-negative).ToString());
        Assert.Equal(4, negative.Precision);
        Assert.True(DatabricksDecimal.Zero.IsZero);
        Assert.Equal(1, DatabricksDecimal.Zero.Precision);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("9", 1)]
    [InlineData("10", 2)]
    [InlineData("100", 3)]
    [InlineData("999", 3)]
    [InlineData("1000", 4)]
    [InlineData("0.001", 1)]
    public void Precision_counts_digits_correctly(string input, int expected)
    {
        Assert.Equal(expected, DatabricksDecimal.Parse(input).Precision);
    }

    [Fact]
    public void Long_implicit_conversion_works()
    {
        DatabricksDecimal value = long.MaxValue;
        Assert.Equal("9223372036854775807", value.ToString());
    }
}
