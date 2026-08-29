using System.Data.Common;

namespace Databricks.AdoNet;

/// <summary>
/// Exception thrown by the Databricks ADO.NET provider for transport, authentication,
/// and statement execution failures.
/// </summary>
public sealed class DatabricksException : DbException
{
    /// <summary>Creates an exception with a message.</summary>
    public DatabricksException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and HTTP status code.</summary>
    public DatabricksException(string message, int statusCode)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>Creates an exception with a message and inner exception.</summary>
    public DatabricksException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>HTTP status code of the failing response, if applicable.</summary>
    public int StatusCode { get; init; }

    /// <summary>Databricks error code (e.g. <c>BAD_REQUEST</c>, <c>RESOURCE_EXHAUSTED</c>), if reported.</summary>
    public string? DatabricksErrorCode { get; init; }

    /// <summary>The identifier of the statement that failed, if applicable.</summary>
    public string? StatementId { get; init; }

    /// <inheritdoc />
    public override string? SqlState { get; }

    /// <summary>Creates an exception carrying full Databricks error details.</summary>
    public DatabricksException(string message, int statusCode, string? errorCode, string? sqlState, string? statementId)
        : base(message)
    {
        StatusCode = statusCode;
        DatabricksErrorCode = errorCode;
        SqlState = sqlState;
        StatementId = statementId;
    }
}
