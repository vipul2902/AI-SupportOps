using System.ComponentModel.DataAnnotations;

namespace AISupportOps.Application.Documents;

public sealed class DocumentOptions
{
    public const string SectionName = "Documents";

    /// <summary>Upper bound on a single upload. Enforced while streaming, not from Content-Length.</summary>
    [Range(1024, 200 * 1024 * 1024)]
    public long MaxFileSizeBytes { get; set; } = 20 * 1024 * 1024;
}
