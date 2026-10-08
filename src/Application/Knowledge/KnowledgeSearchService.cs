using System.Diagnostics;
using System.Text.RegularExpressions;
using AISupportOps.Application.Common;

namespace AISupportOps.Application.Knowledge;

/// <summary>
/// Entry point for semantic search: query preprocessing, then tenant-scoped retrieval.
/// Used by the search API now, and by RAG and the agent's SearchKnowledgeBase tool later.
/// </summary>
public sealed partial class KnowledgeSearchService(IRetrievalService retrieval, ICurrentUser currentUser)
{
    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken ct)
    {
        currentUser.RequireTenantId();
        var query = Preprocess(request.Query);
        if (query.Length == 0)
        {
            throw new BusinessRuleException("Query must contain text.");
        }

        var stopwatch = Stopwatch.StartNew();
        var results = await retrieval.SearchAsync(
            new RetrievalQuery(query, Math.Clamp(request.TopK, 1, RetrievalQuery.MaxTopK), request.DocumentIds ?? [], request.MinScore),
            ct);

        return new SearchResponse(query, results, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>Collapses whitespace and strips control characters so equivalent queries embed identically.</summary>
    public static string Preprocess(string query)
    {
        var cleaned = new string(query.Where(c => !char.IsControl(c) || c is '\n' or '\t').ToArray());
        return Whitespace().Replace(cleaned, " ").Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
