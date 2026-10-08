using System.Text;
using AISupportOps.Application.Documents;
using AISupportOps.Domain.Documents;

namespace AISupportOps.UnitTests.Documents;

public class FileTypeInspectorTests
{
    [Theory]
    [InlineData("guide.pdf", "%PDF-1.7\n...", DocumentKind.Pdf)]
    [InlineData("notes.TXT", "Reset your password from Settings.", DocumentKind.PlainText)]
    [InlineData("faq.md", "# FAQ\n\nÜnïcödé is fine ✓", DocumentKind.Markdown)]
    public void Accepts_allowed_types_with_matching_content(string fileName, string content, DocumentKind expected)
    {
        var result = FileTypeInspector.Inspect(fileName, Encoding.UTF8.GetBytes(content));

        Assert.True(result.IsAllowed, result.Error);
        Assert.Equal(expected, result.Kind);
    }

    [Fact]
    public void Accepts_docx_zip_signature()
    {
        var result = FileTypeInspector.Inspect("policy.docx", [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00]);

        Assert.True(result.IsAllowed);
        Assert.Equal(DocumentKind.Docx, result.Kind);
    }

    [Theory]
    [InlineData("malware.exe")]
    [InlineData("script.html")]
    [InlineData("noextension")]
    [InlineData("archive.pdf.exe")]
    public void Rejects_disallowed_extensions(string fileName) =>
        Assert.False(FileTypeInspector.Inspect(fileName, "hello"u8).IsAllowed);

    [Fact]
    public void Rejects_executable_renamed_to_pdf()
    {
        byte[] peHeader = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00]; // "MZ" Windows executable

        var result = FileTypeInspector.Inspect("invoice.pdf", peHeader);

        Assert.False(result.IsAllowed);
        Assert.Contains("does not match", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_binary_disguised_as_text() =>
        Assert.False(FileTypeInspector.Inspect("notes.txt", [0x48, 0x69, 0x00, 0x01, 0x02]).IsAllowed);

    [Fact]
    public void Rejects_invalid_utf8_text() =>
        Assert.False(FileTypeInspector.Inspect("notes.txt", [0x48, 0xFF, 0xFE, 0x69]).IsAllowed);

    [Fact]
    public void Accepts_text_whose_header_cuts_a_multibyte_character()
    {
        var bytes = Encoding.UTF8.GetBytes("price: €"); // € is 3 bytes
        var truncated = bytes.AsSpan(0, bytes.Length - 1);

        Assert.True(FileTypeInspector.Inspect("notes.txt", truncated).IsAllowed);
    }

    [Fact]
    public void Rejects_empty_file() =>
        Assert.False(FileTypeInspector.Inspect("empty.txt", ReadOnlySpan<byte>.Empty).IsAllowed);
}
