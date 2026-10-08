using System.Collections.Concurrent;
using System.Reflection;

namespace AISupportOps.Application.Ai;

/// <summary>
/// Prompts live in versioned files (Prompts/{name}.v{n}.md, embedded in the assembly), not in code.
/// The version actually used is recorded with every answer, so quality changes can be traced to
/// prompt changes and evaluated (Phase 11) before a new version becomes the default.
/// </summary>
public static class PromptLibrary
{
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);

    public static string Get(string promptId) =>
        Cache.GetOrAdd(promptId, static id =>
        {
            var assembly = typeof(PromptLibrary).Assembly;
            var resource = assembly.GetManifestResourceNames()
                .SingleOrDefault(n => n.EndsWith($".Prompts.{id}.md", StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"Prompt '{id}' not found.");

            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Trim();
        });

    public static string Render(string promptId, IReadOnlyDictionary<string, string> variables)
    {
        var text = Get(promptId);
        foreach (var (key, value) in variables)
        {
            text = text.Replace("{{" + key + "}}", value, StringComparison.Ordinal);
        }

        return text;
    }
}
