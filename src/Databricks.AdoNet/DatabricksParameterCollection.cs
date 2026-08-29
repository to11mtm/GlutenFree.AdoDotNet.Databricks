using System.Collections;
using System.Data.Common;

namespace Databricks.AdoNet;

/// <summary>Collection of <see cref="DatabricksParameter"/> instances for a command.</summary>
public sealed class DatabricksParameterCollection : DbParameterCollection
{
    private readonly List<DatabricksParameter> _parameters = [];

    /// <inheritdoc />
    public override int Count => _parameters.Count;

    /// <inheritdoc />
    public override object SyncRoot => ((ICollection)_parameters).SyncRoot;

    /// <summary>Adds a named parameter with a value and returns it.</summary>
    public DatabricksParameter AddWithValue(string parameterName, object? value)
    {
        var parameter = new DatabricksParameter(parameterName, value);
        _parameters.Add(parameter);
        return parameter;
    }

    /// <inheritdoc />
    public override int Add(object value)
    {
        _parameters.Add(Cast(value));
        return _parameters.Count - 1;
    }

    /// <inheritdoc />
    public override void AddRange(Array values)
    {
        foreach (var value in values)
        {
            Add(value!);
        }
    }

    /// <inheritdoc />
    public override void Clear() => _parameters.Clear();

    /// <inheritdoc />
    public override bool Contains(object value) => _parameters.Contains(Cast(value));

    /// <inheritdoc />
    public override bool Contains(string value) => IndexOf(value) >= 0;

    /// <inheritdoc />
    public override void CopyTo(Array array, int index) => ((ICollection)_parameters).CopyTo(array, index);

    /// <inheritdoc />
    public override IEnumerator GetEnumerator() => _parameters.GetEnumerator();

    /// <inheritdoc />
    public override int IndexOf(object value) => _parameters.IndexOf(Cast(value));

    /// <inheritdoc />
    public override int IndexOf(string parameterName)
    {
        var name = (parameterName ?? string.Empty).TrimStart(':');
        return _parameters.FindIndex(p => string.Equals(p.ParameterName, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public override void Insert(int index, object value) => _parameters.Insert(index, Cast(value));

    /// <inheritdoc />
    public override void Remove(object value) => _parameters.Remove(Cast(value));

    /// <inheritdoc />
    public override void RemoveAt(int index) => _parameters.RemoveAt(index);

    /// <inheritdoc />
    public override void RemoveAt(string parameterName)
    {
        var index = IndexOf(parameterName);
        if (index >= 0)
        {
            _parameters.RemoveAt(index);
        }
    }

    /// <inheritdoc />
    protected override DbParameter GetParameter(int index) => _parameters[index];

    /// <inheritdoc />
    protected override DbParameter GetParameter(string parameterName)
    {
        var index = IndexOf(parameterName);
        return index >= 0
            ? _parameters[index]
            : throw new ArgumentException($"Parameter '{parameterName}' not found in the collection.");
    }

    /// <inheritdoc />
    protected override void SetParameter(int index, DbParameter value) => _parameters[index] = Cast(value);

    /// <inheritdoc />
    protected override void SetParameter(string parameterName, DbParameter value)
    {
        var index = IndexOf(parameterName);
        if (index >= 0)
        {
            _parameters[index] = Cast(value);
        }
        else
        {
            _parameters.Add(Cast(value));
        }
    }

    internal IReadOnlyList<Transport.StatementParameter>? ToStatementParameters()
        => _parameters.Count == 0 ? null : _parameters.Select(p => p.ToStatementParameter()).ToArray();

    private static DatabricksParameter Cast(object? value)
        => value as DatabricksParameter
            ?? throw new InvalidCastException($"Expected a {nameof(DatabricksParameter)}, got {value?.GetType().ToString() ?? "null"}.");
}
