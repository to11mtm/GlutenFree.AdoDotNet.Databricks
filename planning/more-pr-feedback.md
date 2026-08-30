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

---

## Status: ALL RESOLVED (2026-08-29)

1. ConnectionTimeout: overridden to return the builder's ConnectTimeout.
2. Reader Close(): now releases Arrow batch/stream (same resources as Dispose).
3. Explicit DbType: CoerceToDbType converts non-null values before inference (SqlDecimal/DatabricksDecimal exempt from narrowing).
4. DatabricksDecimal.Precision: exact digit count (no floating Log10).
5. DECIMAL(39+) parameters: NotSupportedException thrown locally.
6. Nested Arrow values: intervals/durations render as interval strings; unknown types throw NotSupportedException (no fabricated JSON).
7. ExecuteDbDataReaderAsync behavior pass-through: already fixed in prior round (verified).
8. Arrow TIMESTAMP_NTZ Unspecified kind: already fixed in prior round (verified).

Tests: ReviewHardening2Tests (13 new).

---

## Review round 4 (pullrequestreview-5061422892): ALL RESOLVED (2026-08-30)

1. Non-idempotent submission retries: statement-submission POSTs no longer retry on 503
   (which can arrive after the server accepted the work � resending could double-execute
   DML); 429 remains retryable everywhere, 503 remains retryable for idempotent requests
   (status polls, chunk fetches, downloads, cancel). `IsRetryable(status, idempotent)`.
2. Retry clone leak (sync + async): SendWithRetry(Async) now tracks the original request
   (caller-owned) vs clones (loop-owned); the active clone is disposed in a finally block.
3. OAuth torn read: token + expiry now published as one immutable `CachedToken` snapshot
   through a single volatile reference � no old-token/new-expiry interleaving.
4. DatabricksDecimal parameter precision: `max(Precision, Scale)` instead of `Scale + 1`,
   so DECIMAL(38,38) values (38 fractional digits) are accepted.
5. decimal parameter precision: cosmetic leading zero excluded, precision =
   max(significant unscaled digits, scale, 1) � a scale-28 value below one is DECIMAL(28,28),
   not DECIMAL(29,28) (which would round-trip as SqlDecimal).
6. Negative sub-second intervals: FormatDayTimeInterval derives day/time/fraction from one
   signed Int128 total; -0.1s renders as `-0 00:00:00.100000000` (was `.900000000`).
7. MAP keys: every supported atomic key type handled explicitly (bool, ints, floats,
   decimal, date, timestamp, string); anything else throws instead of fabricating "key".
8. num_affected_rows: checked((int)...) in both sync and async paths � counts above
   int.MaxValue now throw OverflowException instead of wrapping.
9. Reader Close(): also clears _jsonRows/_pendingInline/_pendingLinks so a closed reader
   left in scope cannot retain a whole result payload.
10. README type table: TIMESTAMP (Kind=Utc) and TIMESTAMP_NTZ (Kind=Unspecified) split
    into separate rows.
11. Legacy schema sweep: now double-gated � requires DATABRICKS_SWEEP_LEGACY_SCHEMAS=1
    plus an exact legacy-shape regex match (^adonet_[a-z0-9_]+_[0-9a-f]{32}$); fixed
    adodotnet_* schemas and unrelated adonet_-prefixed names are never dropped.
12. PR description drift (throwaway vs fixed schemas): User manually edited PR.

Tests: ReviewHardening3Tests (new: interval sign matrix, decimal precision matrix,
leading-zero regression) + RestStatementTransportTests (submission-503-no-retry,
poll-503-retry) + DecimalTests DECIMAL(38,38) acceptance. Full suite green including
49 live integration tests.
