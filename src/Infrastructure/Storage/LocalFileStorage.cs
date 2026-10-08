using System.ComponentModel.DataAnnotations;
using AISupportOps.Application.Documents;
using Microsoft.Extensions.Options;

namespace AISupportOps.Infrastructure.Storage;

public sealed class LocalFileStorageOptions
{
    public const string SectionName = "Storage:Local";

    [Required]
    public string RootPath { get; set; } = "data/uploads";
}

/// <summary>
/// Development storage on local disk. Production swaps in Azure Blob Storage behind the same
/// interface. Keys are validated and resolved strictly inside the root (no path traversal).
/// </summary>
internal sealed class LocalFileStorage(IOptions<LocalFileStorageOptions> options) : IFileStorage
{
    private readonly string _root = Path.GetFullPath(options.Value.RootPath);

    public async Task SaveAsync(string key, Stream content, CancellationToken ct)
    {
        var path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Write to a temp file then move, so readers never observe a partially written file.
        var temp = path + ".partial";
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await content.CopyToAsync(file, ct);
            }

            File.Move(temp, path, overwrite: false);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        var path = Resolve(key);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Stored file not found.", key);
        }

        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        File.Delete(Resolve(key));
        return Task.CompletedTask;
    }

    private string Resolve(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(key))
        {
            throw new ArgumentException("Invalid storage key.", nameof(key));
        }

        var full = Path.GetFullPath(Path.Combine(_root, key));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("Storage key resolves outside the storage root.", nameof(key));
        }

        return full;
    }
}
