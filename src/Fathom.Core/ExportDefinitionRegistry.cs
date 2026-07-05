using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Fathom.Core;

/// <summary>Live catalog of registered export definitions. Thread-safe; updated at runtime through the API.</summary>
public interface IExportDefinitionRegistry
{
    void Register(ExportDefinition definition);

    bool Remove(string name);

    ExportDefinition? Find(string name);

    IReadOnlyList<ExportDefinition> All { get; }

    ExportDefinition GetRequired(string name) =>
        Find(name) ?? throw new KeyNotFoundException($"Export definition '{name}' is not registered.");
}

public sealed class ExportDefinitionRegistry : IExportDefinitionRegistry
{
    private readonly ConcurrentDictionary<string, ExportDefinition> _definitions = new(StringComparer.OrdinalIgnoreCase);

    public void Register(ExportDefinition definition)
    {
        var errors = definition.Validate();
        if (errors.Count > 0)
        {
            throw new ExportDefinitionException(
                $"Export definition '{definition.Name}' is invalid:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", errors)}");
        }

        _definitions[definition.Name] = definition;
    }

    public bool Remove(string name) => _definitions.TryRemove(name, out _);

    public ExportDefinition? Find(string name) =>
        _definitions.TryGetValue(name, out var definition) ? definition : null;

    public IReadOnlyList<ExportDefinition> All => [.. _definitions.Values.OrderBy(d => d.Name)];
}

/// <summary>An export definition failed structural validation.</summary>
public sealed class ExportDefinitionException(string message) : Exception(message);

public static class ExportDefinitionSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static string Serialize(ExportDefinition definition) => JsonSerializer.Serialize(definition, Options);

    public static async Task<ExportDefinition> DeserializeAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<ExportDefinition>(stream, Options, cancellationToken)
                ?? throw new ExportDefinitionException("The export definition document is empty.");
        }
        catch (JsonException ex)
        {
            throw new ExportDefinitionException($"The export definition is not valid JSON: {ex.Message}");
        }
    }
}

/// <summary>File-based export definition persistence: one JSON document per definition, so they can be versioned in git.</summary>
public sealed class ExportDefinitionDirectoryStore(string directory, ILogger<ExportDefinitionDirectoryStore> logger)
{
    public async Task<IReadOnlyList<ExportDefinition>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var definitions = new List<ExportDefinition>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(file);
                definitions.Add(await ExportDefinitionSerializer.DeserializeAsync(stream, cancellationToken));
            }
            catch (ExportDefinitionException ex)
            {
                logger.LogError(ex, "Skipping invalid export definition {File}", file);
            }
        }

        return definitions;
    }

    public async Task SaveAsync(ExportDefinition definition, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(PathFor(definition.Name), ExportDefinitionSerializer.Serialize(definition), cancellationToken);
    }

    public Task DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        var path = PathFor(name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string PathFor(string name)
    {
        // A registered name is a validated XML NCName, which is already a safe, injective file
        // name (letters/digits/_/-/. only, and — since '.' is not a valid start char — never
        // "." or ".."). Use it verbatim rather than sanitizing, which previously mapped '.'
        // to '_' and let "a.b" and "a_b" overwrite each other's files. Reject anything that
        // didn't come through validation, so a hand-crafted name can never escape the directory.
        if (!ExportDefinition.IsValidName(name))
        {
            throw new ArgumentException(
                $"Refusing to derive a file path from unsafe export name '{name}'.", nameof(name));
        }

        return Path.Combine(directory, $"{name}.json");
    }
}
