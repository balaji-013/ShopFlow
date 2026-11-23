namespace ShopFlow.Domain.Common;

/// <summary>Base for every persisted entity. Guid keys are generated in the app (no DB round trip, safe to expose in URLs).</summary>
public abstract class Entity
{
    public Guid Id { get; protected set; } = Guid.NewGuid();
}

/// <summary>Entities that belong to one tenant. EF applies a global query filter on this interface (see AppDbContext).</summary>
public interface ITenantOwned
{
    Guid TenantId { get; }
}

/// <summary>
/// A business rule was violated. The API maps it to HTTP 422 (the request is well-formed but semantically invalid),
/// whereas malformed input is 400 (validation) - see ExceptionHandler.
/// </summary>
public class DomainException(string message, string code = "domain_error") : Exception(message)
{
    public string Code { get; } = code;
}
