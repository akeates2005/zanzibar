using Poc.Application.Authorization;

namespace Poc.IntegrationTests.Platform;

/// <summary>
/// Reads authz/validation.yaml, the single source of truth for the seed tuples and the access matrix, so the .NET tests
/// and <c>zed validate</c> can never disagree about what the PRD says.
/// </summary>
public static class ValidationYaml
{
    public sealed record Assertion(Relationship Relationship, bool Expected);

    public static (IReadOnlyList<Relationship> Seed, IReadOnlyList<Assertion> Assertions) Load()
    {
        var seed = new List<Relationship>();
        var assertions = new List<Assertion>();
        string section = "";
        foreach (var raw in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "authz", "validation.yaml")))
        {
            var line = raw.TrimEnd();
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#') || trimmed.Length == 0) continue;
            if (trimmed.StartsWith("relationships:", StringComparison.Ordinal)) { section = "relationships"; continue; }
            if (trimmed.StartsWith("assertTrue:", StringComparison.Ordinal)) { section = "true"; continue; }
            if (trimmed.StartsWith("assertFalse:", StringComparison.Ordinal)) { section = "false"; continue; }
            if (trimmed.StartsWith("schemaFile:", StringComparison.Ordinal) || trimmed.StartsWith("assertions:", StringComparison.Ordinal)) continue;

            switch (section)
            {
                case "relationships":
                    seed.Add(ParseTuple(trimmed));
                    break;
                case "true" or "false" when trimmed.StartsWith("- \"", StringComparison.Ordinal):
                    assertions.Add(new Assertion(ParseTuple(trimmed[3..^1]), section == "true"));
                    break;
            }
        }

        return (seed, assertions);
    }

    /// <summary>Parses <c>type:id#relation@type:id</c> or <c>type:id#relation@type:id#relation</c>.</summary>
    public static Relationship ParseTuple(string text)
    {
        var at = text.IndexOf('@', StringComparison.Ordinal);
        var left = text[..at];
        var right = text[(at + 1)..];
        var hash = left.IndexOf('#', StringComparison.Ordinal);
        var resource = left[..hash].Split(':');
        var relation = left[(hash + 1)..];
        var subjectHash = right.IndexOf('#', StringComparison.Ordinal);
        var subject = (subjectHash < 0 ? right : right[..subjectHash]).Split(':');
        var subjectRelation = subjectHash < 0 ? null : right[(subjectHash + 1)..];
        return new Relationship(new ResourceRef(resource[0], resource[1]), relation, new SubjectRef(subject[0], subject[1], subjectRelation));
    }
}
