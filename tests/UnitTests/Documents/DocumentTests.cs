using AISupportOps.Application.Documents;
using AISupportOps.Domain.Documents;

namespace AISupportOps.UnitTests.Documents;

public class DocumentTests
{
    [Theory]
    [InlineData("../../etc/passwd.txt", "passwd.txt")]
    [InlineData(@"C:\Users\me\secret.pdf", "secret.pdf")]
    [InlineData("bad<name>|?.md", "bad_name___.md")]
    [InlineData("  ...  ", "document")]
    [InlineData(null, "document")]
    public void FileNames_are_sanitized(string? input, string expected) =>
        Assert.Equal(expected, FileNames.Sanitize(input));

    [Fact]
    public void Long_file_names_are_truncated_keeping_extension()
    {
        var name = FileNames.Sanitize(new string('a', 400) + ".pdf");

        Assert.Equal(Document.FileNameMaxLength, name.Length);
        Assert.EndsWith(".pdf", name, StringComparison.Ordinal);
    }

    [Fact]
    public void Lifecycle_uploaded_to_processed()
    {
        var doc = NewDocument();
        var now = DateTimeOffset.UtcNow;

        doc.BeginProcessingAttempt();
        doc.MarkProcessed(12, now);

        Assert.Equal(DocumentStatus.Processed, doc.Status);
        Assert.Equal(12, doc.ChunkCount);
        Assert.Equal(now, doc.ProcessedAt);
    }

    [Fact]
    public void Failed_document_can_be_requeued_which_resets_attempts_and_error()
    {
        var doc = NewDocument();
        doc.BeginProcessingAttempt();
        doc.MarkFailed("extraction failed");

        doc.Requeue();

        Assert.Equal(DocumentStatus.Uploaded, doc.Status);
        Assert.Equal(0, doc.ProcessingAttempts);
        Assert.Null(doc.Error);
    }

    [Fact]
    public void Expired_lease_allows_reclaiming_a_processing_document_and_counts_attempts()
    {
        var doc = NewDocument();
        doc.BeginProcessingAttempt();

        doc.BeginProcessingAttempt();

        Assert.Equal(2, doc.ProcessingAttempts);
    }

    [Fact]
    public void Invalid_transitions_throw()
    {
        var doc = NewDocument();

        Assert.Throws<InvalidOperationException>(() => doc.MarkProcessed(1, DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(doc.Requeue);
        doc.BeginProcessingAttempt();
        doc.MarkProcessed(1, DateTimeOffset.UtcNow);
        Assert.Throws<InvalidOperationException>(doc.BeginProcessingAttempt);
    }

    private static Document NewDocument() =>
        new(Guid.NewGuid(), "a.txt", DocumentKind.PlainText, "text/plain", 10, new string('0', 64), "t/d", Guid.NewGuid());
}
