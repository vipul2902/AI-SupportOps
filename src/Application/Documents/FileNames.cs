using System.Text;
using AISupportOps.Domain.Documents;

namespace AISupportOps.Application.Documents;

public static class FileNames
{
    /// <summary>
    /// Produces a display-safe file name: strips any directory components ("../../etc/passwd"),
    /// control and reserved characters, and caps length while keeping the extension.
    /// </summary>
    public static string Sanitize(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/').Split('/')[^1]);
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(char.IsControl(c) || "<>:\"|?*".Contains(c, StringComparison.Ordinal) ? '_' : c);
        }

        var cleaned = builder.ToString().Trim().Trim('.');
        if (cleaned.Length == 0)
        {
            return "document";
        }

        if (cleaned.Length > Document.FileNameMaxLength)
        {
            var extension = Path.GetExtension(cleaned);
            cleaned = cleaned[..(Document.FileNameMaxLength - extension.Length)] + extension;
        }

        return cleaned;
    }
}
