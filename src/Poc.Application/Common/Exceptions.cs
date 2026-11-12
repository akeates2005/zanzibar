using Poc.Application.Authorization;

namespace Poc.Application.Common;

/// <summary>The object does not exist, or the caller may not know that it does. Maps to HTTP 404.</summary>
public sealed class NotFoundException(string what) : Exception($"{what} was not found.");

/// <summary>The request conflicts with current state (children exist, concurrent edit). Maps to HTTP 409.</summary>
public sealed class ConflictException : Exception
{
    public ConflictException(string message)
        : base(message)
    {
    }

    public ConflictException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>A rule the schema cannot express, or a malformed grant, refused before anything was written. Maps to 422.</summary>
public sealed class RuleViolationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>The caller lacks a permission the command requires. Maps to 403 naming <see cref="Permission"/>.</summary>
public sealed class PermissionDeniedException(string permission, ResourceRef resource)
    : Exception($"Missing permission '{permission}' on {resource}.")
{
    public string Permission { get; } = permission;

    public ResourceRef Resource { get; } = resource;
}
