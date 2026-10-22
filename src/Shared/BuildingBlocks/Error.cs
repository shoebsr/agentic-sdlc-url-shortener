namespace BuildingBlocks;

public enum ErrorKind
{
    Validation,
    NotFound,
    Conflict,
    Gone,
    Forbidden,
    Unavailable,
}

public sealed record Error(string Code, string Message, ErrorKind Kind)
{
    public static Error Validation(string code, string message) => new(code, message, ErrorKind.Validation);
    public static Error NotFound(string code, string message) => new(code, message, ErrorKind.NotFound);
    public static Error Conflict(string code, string message) => new(code, message, ErrorKind.Conflict);
    public static Error Gone(string code, string message) => new(code, message, ErrorKind.Gone);
    public static Error Forbidden(string code, string message) => new(code, message, ErrorKind.Forbidden);
    public static Error Unavailable(string code, string message) => new(code, message, ErrorKind.Unavailable);
}
