src/GlutenFree.Databricks.AdoNet/DatabricksConnection.cs:78

    DbConnection.ConnectionTimeout is not overridden, so callers see the framework's default value (15 seconds) while Open actually enforces the configured ConnectTimeout (30 seconds by default). Expose the builder value so provider metadata matches behavior.

    /// <summary>Default command timeout (seconds) inherited by commands created from this connection.</summary>
    public int DefaultCommandTimeout => _builder.CommandTimeout;

src/GlutenFree.Databricks.AdoNet/DatabricksDataReader.cs:391

    Close() marks the reader closed but leaves its current Arrow batch and stream reader undisposed. Code that follows the normal ADO.NET reader.Close() pattern retains buffers/resources until a later Dispose; release the same resources here that Dispose(bool) releases.
    src/GlutenFree.Databricks.AdoNet/DatabricksParameter.cs:102
    For non-null values this switch always infers from the CLR runtime type and ignores an explicitly assigned DbType, despite the documented override contract. For example, an int with DbType.Int64 is sent as INT, not BIGINT. Apply DbType conversion/coercion before inference whenever it is not DbType.Object.
    src/GlutenFree.Databricks.AdoNet/DatabricksDecimal.cs:59
    Precision is an exact integer property, but it is derived through floating-point Log10; values near powers of ten can be rounded to the wrong side of an integer and report an off-by-one digit count. This also feeds generated DECIMAL(p,s) parameter types. Count the invariant decimal digits directly instead.
    src/GlutenFree.Databricks.AdoNet/DatabricksParameter.cs:123
    An arbitrary-precision value wider than Databricks' maximum precision is emitted as DECIMAL(39,...) (or larger), which the server rejects. Validate the computed precision/scale and throw locally once it exceeds 38 so parameter failures are deterministic and clearly diagnosed.
    src/GlutenFree.Databricks.AdoNet/DatabricksTypeMap.cs:273
    Unsupported child arrays are silently serialized as their CLR type name (for example, an interval nested inside an ARRAY becomes "YearMonthIntervalArray") instead of the actual value. This corrupts otherwise supported complex results. Handle the same Databricks Arrow scalar types recursively here, or throw NotSupportedException rather than returning fabricated JSON.

src/GlutenFree.Databricks.AdoNet/DatabricksCommand.cs:157

    The async reader overload also discards behavior, so ExecuteReaderAsync(CommandBehavior.CloseConnection, ...) does not close the connection when the returned reader is closed. Pass the behavior through to the reader just as for the synchronous overload.

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior, CancellationToken cancellationToken)
        => await ExecuteReaderInternalAsync(cancellationToken).ConfigureAwait(false);

src/GlutenFree.Databricks.AdoNet/DatabricksTypeMap.cs:95

    The Arrow path always returns UtcDateTime, including for TIMESTAMP_NTZ. That causes an NTZ value read from Databricks to be rebound as a zoned TIMESTAMP and can alter its wall-clock meaning. Branch on the manifest type and return an Unspecified DateTime for NTZ.
