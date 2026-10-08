using System.Text.Json;

namespace BatchAcCore.Core;

/// <summary>
/// Application-owned, vetted AutoCAD command lines that can be appended to an execution-definition run.
/// The developer-maintained JSON source is embedded during compilation, so profiles persist identifiers only.
/// </summary>
public sealed record ScriptAdditionDefinition(string Id, string Name, string Description, string ScriptLine);

public static class ScriptAdditionLibrary
{
    private const string ResourceName = "BatchAcCore.ScriptAdditionLibrary.json";
    private static readonly Lazy<IReadOnlyList<ScriptAdditionDefinition>> Definitions = new(LoadDefinitions);

    public static IReadOnlyList<ScriptAdditionDefinition> Entries => Definitions.Value;

    public static IReadOnlyList<ScriptAdditionDefinition> Resolve(IEnumerable<string>? ids)
    {
        if (ids is null)
            return [];

        var resolved = new List<ScriptAdditionDefinition>();
        var selectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("ScriptAdditionIds cannot contain a blank library identifier.");
            if (!selectedIds.Add(id))
                throw new ArgumentException($"ScriptAdditionIds contains the duplicate library identifier '{id}'.");

            var definition = Entries.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
            if (definition is null)
                throw new ArgumentException($"ScriptAdditionIds contains unknown library identifier '{id}'.");
            resolved.Add(definition);
        }

        return resolved;
    }

    private static IReadOnlyList<ScriptAdditionDefinition> LoadDefinitions()
    {
        using var stream = typeof(ScriptAdditionLibrary).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded script-addition library resource '{ResourceName}' was not found.");
        var definitions = JsonSerializer.Deserialize<List<ScriptAdditionDefinition>>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("The embedded script-addition library is empty.");

        if (definitions.Count == 0)
            throw new InvalidOperationException("The embedded script-addition library must contain at least one entry.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            if (string.IsNullOrWhiteSpace(definition.Id) || string.IsNullOrWhiteSpace(definition.Name) || string.IsNullOrWhiteSpace(definition.ScriptLine))
                throw new InvalidOperationException("Every script-addition library entry requires an id, name, and scriptLine.");
            if (!ids.Add(definition.Id))
                throw new InvalidOperationException($"The embedded script-addition library contains duplicate id '{definition.Id}'.");
            if (definition.ScriptLine.Contains('\r') || definition.ScriptLine.Contains('\n'))
                throw new InvalidOperationException($"Script-addition library entry '{definition.Id}' must contain exactly one script line.");
        }

        return definitions;
    }
}
