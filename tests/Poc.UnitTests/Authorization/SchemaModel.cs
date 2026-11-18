using System.Text.RegularExpressions;
using Poc.Application.Authorization;

namespace Poc.UnitTests.Authorization;

/// <summary>
/// A minimal reader for authz/schema.zed: definitions with their relation and permission names, and the same definitions in
/// the shape the engine reflects them (<see cref="Definitions"/>). An integration test
/// (<c>SchemaReflectionTests</c>) proves that shape equals what SpiceDB's ReflectSchema returns for this file.
/// </summary>
public sealed partial class SchemaModel
{
    private SchemaModel(
        IReadOnlyDictionary<string, (SortedSet<string> Relations, SortedSet<string> Permissions)> types,
        IReadOnlyList<ReflectedDefinition> definitions)
    {
        Types = types;
        Definitions = definitions;
    }

    public IReadOnlyDictionary<string, (SortedSet<string> Relations, SortedSet<string> Permissions)> Types { get; }

    /// <summary>Every definition with its relations' allowed subjects and its permissions, in file order.</summary>
    public IReadOnlyList<ReflectedDefinition> Definitions { get; }

    public static SchemaModel Load() =>
        Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "authz", "schema.zed")));

    public static SchemaModel Parse(string schema)
    {
        var types = new Dictionary<string, (SortedSet<string>, SortedSet<string>)>();
        var definitions = new List<ReflectedDefinition>();
        foreach (Match definition in DefinitionRegex().Matches(schema))
        {
            var body = definition.Groups["body"].Value;
            var relations = RelationRegex().Matches(body)
                .Select(m => new ReflectedRelation(m.Groups["name"].Value, ParseSubjects(m.Groups["subjects"].Value)))
                .ToList();
            var permissions = PermissionRegex().Matches(body).Select(m => m.Groups["name"].Value).ToList();

            var name = definition.Groups["name"].Value;
            types[name] = (new SortedSet<string>(relations.Select(r => r.Name), StringComparer.Ordinal), new SortedSet<string>(permissions, StringComparer.Ordinal));
            definitions.Add(new ReflectedDefinition(name, relations, permissions));
        }

        return new SchemaModel(types, definitions);
    }

    /// <summary><c>user | group#member | user:* | user with some_caveat</c>.</summary>
    private static List<AllowedSubject> ParseSubjects(string text) =>
        text.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(part =>
            {
                var m = SubjectRegex().Match(part);
                if (!m.Success) throw new FormatException($"Unrecognised subject type '{part}'.");
                return new AllowedSubject(
                    m.Groups["type"].Value,
                    m.Groups["relation"].Success ? m.Groups["relation"].Value : null,
                    m.Groups["wildcard"].Success,
                    m.Groups["caveat"].Success ? m.Groups["caveat"].Value : null);
            })
            .ToList();

    // A type name may carry one prefix, e.g. `commerce/wallet` (DECISIONS.md, "Team-prefixed object types").
    [GeneratedRegex(@"definition\s+(?<name>[a-z][a-z0-9_]*(?:/[a-z][a-z0-9_]*)?)\s*\{(?<body>[^}]*)\}")]
    private static partial Regex DefinitionRegex();

    [GeneratedRegex(@"^\s*relation\s+(?<name>\w+)\s*:(?<subjects>[^\r\n]*)$", RegexOptions.Multiline)]
    private static partial Regex RelationRegex();

    [GeneratedRegex(@"^\s*permission\s+(?<name>\w+)\s*=", RegexOptions.Multiline)]
    private static partial Regex PermissionRegex();

    [GeneratedRegex(@"^(?<type>[a-z][a-z0-9_]*(?:/[a-z][a-z0-9_]*)?)(?:#(?<relation>\w+)|(?<wildcard>:\*))?(?:\s+with\s+(?<caveat>\w+))?$")]
    private static partial Regex SubjectRegex();
}
