namespace LoginAndReg.Services;

/// <summary>
/// Thrown when an uploaded .glb file fails the magic-byte validation
/// (header "glTF", version 2, consistent length). Mapped to 400
/// ProblemDetails by the API controller.
/// </summary>
public class InvalidGlbFileException : Exception
{
    public InvalidGlbFileException(string message)
        : base(message)
    {
    }
}
