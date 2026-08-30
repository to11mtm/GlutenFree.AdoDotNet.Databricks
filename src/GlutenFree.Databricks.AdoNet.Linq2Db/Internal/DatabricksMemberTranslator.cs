using LinqToDB;
using LinqToDB.Internal.DataProvider.Translation;
using LinqToDB.Internal.SqlQuery;
using LinqToDB.Linq.Translation;

namespace GlutenFree.Databricks.AdoNet.Linq2Db.Internal;

/// <summary>
/// Member translator for Databricks SQL. Adds Databricks date/time function translations
/// on top of linq2db's defaults.
/// </summary>
public sealed class DatabricksMemberTranslator : ProviderMemberTranslatorDefault
{
    /// <inheritdoc />
    protected override IMemberTranslator CreateDateMemberTranslator()
        => new DateFunctionsTranslator();

    private sealed class DateFunctionsTranslator : DateFunctionsTranslatorBase
    {
        protected override ISqlExpression? TranslateDateTimeTruncationToDate(
            ITranslationContext translationContext, ISqlExpression dateExpression, TranslationFlags translationFlags)
        {
            // DateTime.Date -> DATE_TRUNC('DAY', x) (stays a TIMESTAMP at midnight).
            var factory = translationContext.ExpressionFactory;
            var dbType = factory.GetDbDataType(dateExpression);
            return factory.Function(
                dbType,
                "DATE_TRUNC",
                factory.Value(factory.GetDbDataType(typeof(string)), "DAY"),
                dateExpression);
        }

        protected override ISqlExpression? TranslateDateTimeDatePart(
            ITranslationContext translationContext,
            TranslationFlags translationFlag,
            ISqlExpression dateTimeExpression,
            Sql.DateParts datepart)
            => TranslatePart(translationContext, dateTimeExpression, datepart);

        protected override ISqlExpression? TranslateDateTimeOffsetDatePart(
            ITranslationContext translationContext,
            TranslationFlags translationFlag,
            ISqlExpression dateTimeExpression,
            Sql.DateParts datepart)
            => TranslatePart(translationContext, dateTimeExpression, datepart);

        protected override ISqlExpression? TranslateDateOnlyDatePart(
            ITranslationContext translationContext,
            TranslationFlags translationFlag,
            ISqlExpression dateTimeExpression,
            Sql.DateParts datepart)
            => TranslatePart(translationContext, dateTimeExpression, datepart);

        private static ISqlExpression? TranslatePart(
            ITranslationContext translationContext, ISqlExpression expression, Sql.DateParts datepart)
        {
            var functionName = datepart switch
            {
                Sql.DateParts.Year => "YEAR",
                Sql.DateParts.Quarter => "QUARTER",
                Sql.DateParts.Month => "MONTH",
                Sql.DateParts.DayOfYear => "DAYOFYEAR",
                Sql.DateParts.Day => "DAY",
                Sql.DateParts.Week => "WEEKOFYEAR",
                Sql.DateParts.WeekDay => "DAYOFWEEK",
                Sql.DateParts.Hour => "HOUR",
                Sql.DateParts.Minute => "MINUTE",
                Sql.DateParts.Second => "SECOND",
                _ => null,
            };

            if (functionName is null)
            {
                return null;
            }

            var factory = translationContext.ExpressionFactory;
            return factory.Function(factory.GetDbDataType(typeof(int)), functionName, expression);
        }
    }
}
