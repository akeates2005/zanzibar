using System.Text.RegularExpressions;

namespace Poc.Domain.Common;

/// <summary>
/// Identifiers are strings used verbatim as database keys and as SpiceDB object ids. Generated ones are version 7
/// GUIDs in lower case without dashes; seed data uses readable ones such as <c>acme</c>.
/// </summary>
public static partial class EntityId
{
    public const int MaxLength = 128;

    public static string New() => Guid.CreateVersion7().ToString("N");

    /// <summary>
    /// Ids must be safe to embed in a tuple: lower-case letters, digits, '-' and '_' only, starting with a letter or
    /// digit. This is stricter than the engine's own rule on purpose, so a user-supplied id can never smuggle a
    /// separator such as '#', ':' or '@' into a relationship.
    /// </summary>
    public static string Validate(string? id, string what = "id")
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxLength || !IdRegex().IsMatch(id))
            throw new DomainRuleException(
                $"The {what} must be 1-{MaxLength} characters of lower-case letters, digits, '-' and '_', starting with a letter or digit.");
        return id;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]*$")]
    private static partial Regex IdRegex();
}
