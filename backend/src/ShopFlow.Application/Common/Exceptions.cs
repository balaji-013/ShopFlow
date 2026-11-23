namespace ShopFlow.Application.Common;

// Each exception maps to exactly one HTTP status in the API's global ExceptionHandler.
public abstract class AppException(string message, string code) : Exception(message) { public string Code { get; } = code; }
public class NotFoundException(string what) : AppException($"{what} was not found.", "not_found");
public class ConflictException(string message, string code = "conflict") : AppException(message, code);
public class UnauthorizedAppException(string message = "Invalid credentials.") : AppException(message, "unauthorized");
public class ForbiddenAppException(string message = "You do not have access to this resource.") : AppException(message, "forbidden");
public class UnprocessableException(string message, string code) : AppException(message, code);

public record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor);
