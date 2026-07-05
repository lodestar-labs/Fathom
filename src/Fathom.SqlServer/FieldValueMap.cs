using System.Collections;
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using Fathom.Core;

namespace Fathom.SqlServer;

/// <summary>
/// The field name → position map for one entity, built once per export run and shared by
/// every row of that entity.
/// </summary>
internal sealed class FieldSchema
{
    private FieldSchema(string[] names, FrozenDictionary<string, int> index)
    {
        Names = names;
        Index = index;
    }

    public string[] Names { get; }

    public FrozenDictionary<string, int> Index { get; }

    public static FieldSchema For(EntityDefinition entity)
    {
        var names = entity.Fields.Select(f => f.Name).ToArray();
        var index = names
            .Select((name, i) => KeyValuePair.Create(name, i))
            .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        return new FieldSchema(names, index);
    }
}

/// <summary>
/// One row's values as a read-only dictionary, backed by a plain array plus the entity's
/// shared <see cref="FieldSchema"/>. Exports touch this once per (row, field) on the hottest
/// path in the system — compared to allocating a real <see cref="Dictionary{TKey,TValue}"/>
/// per row, this is one small object and one array, with lookups against a frozen
/// (read-optimized) shared index.
/// </summary>
internal sealed class FieldValueMap(FieldSchema schema, object?[] values) : IReadOnlyDictionary<string, object?>
{
    public int Count => values.Length;

    public IEnumerable<string> Keys => schema.Names;

    public IEnumerable<object?> Values => values;

    public object? this[string key] =>
        TryGetValue(key, out var value) ? value : throw new KeyNotFoundException($"Field '{key}' is not part of this entity.");

    public bool ContainsKey(string key) => schema.Index.ContainsKey(key);

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out object? value)
    {
        if (schema.Index.TryGetValue(key, out var i))
        {
            value = values[i];
            return true;
        }

        value = null;
        return false;
    }

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        for (var i = 0; i < values.Length; i++)
        {
            yield return new KeyValuePair<string, object?>(schema.Names[i], values[i]);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
