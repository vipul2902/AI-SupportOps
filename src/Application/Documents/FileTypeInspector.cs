using System.Text;
using AISupportOps.Domain.Documents;

namespace AISupportOps.Application.Documents;

/// <summary>
/// Decides whether an upload is an allowed type by checking BOTH the extension (allowlist)
/// and the file's leading bytes (magic number). The client-supplied Content-Type is ignored:
/// it is trivially spoofable. A renamed executable ("invoice.pdf") fails the signature check.
/// </summary>
public static class FileTypeInspector
{
    /// <summary>Bytes needed from the start of the file to make a decision.</summary>
    public const int HeaderSize = 8 * 1024;

    private static readonly byte[] PdfSignature = "%PDF-"u8.ToArray();
    private static readonly byte[] ZipSignature = [0x50, 0x4B, 0x03, 0x04];

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly Dictionary<string, (DocumentKind Kind, string ContentType)> Allowed =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = (DocumentKind.Pdf, "application/pdf"),
            [".txt"] = (DocumentKind.PlainText, "text/plain"),
            [".md"] = (DocumentKind.Markdown, "text/markdown"),
            [".markdown"] = (DocumentKind.Markdown, "text/markdown"),
            [".docx"] = (DocumentKind.Docx, "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
        };

    public static IReadOnlyCollection<string> AllowedExtensions => Allowed.Keys;

    public static FileTypeResult Inspect(string fileName, ReadOnlySpan<byte> header)
    {
        var extension = Path.GetExtension(fileName);
        if (!Allowed.TryGetValue(extension, out var type))
        {
            return FileTypeResult.Reject($"File type '{extension}' is not supported. Allowed: {string.Join(", ", Allowed.Keys)}.");
        }

        if (header.IsEmpty)
        {
            return FileTypeResult.Reject("File is empty.");
        }

        var signatureOk = type.Kind switch
        {
            DocumentKind.Pdf => header.StartsWith(PdfSignature),
            // DOCX is a ZIP container. Deeper validation (word/document.xml present) happens at extraction.
            DocumentKind.Docx => header.StartsWith(ZipSignature),
            DocumentKind.PlainText or DocumentKind.Markdown => LooksLikeText(header),
            _ => false,
        };

        return signatureOk
            ? FileTypeResult.Accept(type.Kind, type.ContentType)
            : FileTypeResult.Reject($"File content does not match the '{extension}' file type.");
    }

    /// <summary>Text files must be valid UTF-8 and contain no NUL bytes (a strong binary indicator).</summary>
    private static bool LooksLikeText(ReadOnlySpan<byte> header)
    {
        if (header.Contains((byte)0))
        {
            return false;
        }

        // The header may cut a multi-byte character in half; trim up to 3 trailing continuation bytes.
        var end = header.Length;
        for (var i = 0; i < 3 && end > 0 && (header[end - 1] & 0xC0) == 0x80; i++)
        {
            end--;
        }

        if (end > 0 && header[end - 1] >= 0xC0)
        {
            end--; // dangling lead byte
        }

        try
        {
            StrictUtf8.GetCharCount(header[..end]);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}

public sealed record FileTypeResult(bool IsAllowed, DocumentKind Kind, string ContentType, string? Error)
{
    public static FileTypeResult Accept(DocumentKind kind, string contentType) => new(true, kind, contentType, null);

    public static FileTypeResult Reject(string error) => new(false, default, string.Empty, error);
}
