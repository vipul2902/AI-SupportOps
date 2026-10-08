namespace AISupportOps.Application.Common;

/// <summary>
/// Expected, client-caused failures. The API maps each subtype to an HTTP status code;
/// messages are safe to return to the client.
/// </summary>
public abstract class AppException(string message) : Exception(message);

/// <summary>The resource does not exist, or exists in another tenant (indistinguishable by design).</summary>
public sealed class NotFoundException(string message) : AppException(message);

public sealed class ConflictException(string message) : AppException(message);

/// <summary>Authenticated but not permitted.</summary>
public sealed class ForbiddenException(string message) : AppException(message);

/// <summary>Credentials are missing, invalid, or expired.</summary>
public sealed class UnauthorizedException(string message) : AppException(message);

/// <summary>The request is well-formed but violates a business rule.</summary>
public sealed class BusinessRuleException(string message) : AppException(message);

/// <summary>The uploaded file's type is not allowed or its content does not match its extension.</summary>
public sealed class UnsupportedFileException(string message) : AppException(message);

/// <summary>The request body exceeds a configured size limit.</summary>
public sealed class PayloadTooLargeException(string message) : AppException(message);
