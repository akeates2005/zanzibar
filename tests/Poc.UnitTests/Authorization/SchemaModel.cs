using System.Text.RegularExpressions;

namespace Poc.UnitTests.Authorization;

/// <summary>A minimal reader for authz/schema.zed: definitions with their relation and permission names.</summary>
public sealed partial class SchemaModel
{
    private SchemaModel(IReadOnlyDictionary<string, (SortedSet<string> Relations, SortedSet<string> Permissions)> types) =>
        Types = types;

    public IReadOnlyDictionary<string, (SortedSet<string> Relations, SortedSet<string> Permissions)> Types { get; }

    public static SchemaModel Load() =>
        Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "authz", "schema.zed")));

    public static SchemaModel Parse(string schema)
    {
        var types = new Dictionary<string, (SortedSet<string>, SortedSet<string>)>();
        foreach (Match definition in DefinitionRegex().Matches(schema))
        {
            var relations = new SortedSet<string>(StringComparer.Ordinal);
            var permissions = new SortedSet<string>(StringComparer.Ordinal);
            var body = definition.Groups["body"].Value;
            foreach (Match m in RelationRegex().Matches(body)) relations.Add(m.Groups["name"].Value);
            foreach (Match m in PermissionRegex().Matches(body)) permissions.Add(m.Groups["name"].Value);
            types[definition.Groups["name"].Value] = (relations, permissions);
        }

        return new SchemaModel(types);
    }

    [GeneratedRegex(@"definition\s+(?<name>\w+)\s*\{(?<body>[^}]*)\}")]
    private static partial Regex DefinitionRegex();

    [GeneratedRegex(@"^\s*relation\s+(?<name>\w+)\s*:", RegexOptions.Multiline)]
    private static partial Regex RelationRegex();

    [GeneratedRegex(@"^\s*permission\s+(?<name>\w+)\s*=", RegexOptions.Multiline)]
    private static partial Regex PermissionRegex();
}
