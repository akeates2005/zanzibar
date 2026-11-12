using Poc.Domain.Common;

namespace Poc.Domain.Directory;

/// <summary>Directory entry for pickers and the stub issuer. The engine never reads it.</summary>
public sealed class User
{
    private User()
    {
    }

    public string Id { get; private set; } = "";

    public string DisplayName { get; private set; } = "";

    public string Email { get; private set; } = "";

    public static User Create(string id, string displayName, string email)
    {
        if (string.IsNullOrWhiteSpace(displayName)) throw new DomainRuleException("The display name must not be blank.");
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@', StringComparison.Ordinal))
            throw new DomainRuleException("The email address is not valid.");
        return new User { Id = EntityId.Validate(id, "user id"), DisplayName = displayName.Trim(), Email = email.Trim() };
    }
}
