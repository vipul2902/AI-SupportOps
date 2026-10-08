using AISupportOps.Application.Ai;
using AISupportOps.Application.Chat;
using AISupportOps.Application.Ingestion;
using AISupportOps.Domain.Chat;
using AISupportOps.Infrastructure.Ai;

namespace AISupportOps.UnitTests.Chat;

public class ChatBuildingBlockTests
{
    private sealed class WordCounter : ITokenCounter
    {
        public int Count(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid ConversationId = Guid.NewGuid();

    private static Message User(string text) => Message.FromUser(Tenant, ConversationId, text);

    private static Message Assistant(string text, MessageStatus status = MessageStatus.Completed) =>
        Message.FromAssistant(Tenant, ConversationId, text, status, "Answered", null, [], null, "p", null, null, 0);

    [Fact]
    public void History_keeps_newest_turns_within_budget_in_chronological_order()
    {
        var messages = new List<Message>
        {
            User("first question here"), Assistant("first answer here"),
            User("second question here"), Assistant("second answer here"),
        };

        var window = HistoryWindow.Select(messages, maxMessages: 10, maxTokens: 6, new WordCounter());

        Assert.Equal(["second question here", "second answer here"], window.Select(m => m.Content));
        Assert.Equal(LlmRole.User, window[0].Role);
    }

    [Fact]
    public void History_respects_message_cap_and_never_starts_with_an_assistant_reply()
    {
        var messages = new List<Message> { User("q1"), Assistant("a1"), User("q2"), Assistant("a2") };

        var window = HistoryWindow.Select(messages, maxMessages: 3, maxTokens: 1000, new WordCounter());

        // Newest 3 = a1, q2, a2 → orphaned leading a1 dropped.
        Assert.Equal(["q2", "a2"], window.Select(m => m.Content));
    }

    [Fact]
    public void History_strips_old_citation_markers_and_skips_failed_messages()
    {
        var messages = new List<Message> { User("q"), Assistant("Open Settings [1] then Security [2][3]."), User("q2"), Assistant("partial", MessageStatus.Failed) };

        var window = HistoryWindow.Select(messages, 10, 1000, new WordCounter());

        Assert.Equal(["q", "Open Settings then Security.", "q2"], window.Select(m => m.Content));
    }

    [Fact]
    public void Conversation_title_comes_from_first_message_single_line_and_capped()
    {
        var conversation = new Conversation(Tenant, Guid.NewGuid(), "How do I\n reset   my password?" + new string('x', 300), DateTimeOffset.UtcNow);

        Assert.StartsWith("How do I reset my password?", conversation.Title, StringComparison.Ordinal);
        Assert.Equal(Conversation.TitleMaxLength, conversation.Title.Length);
        Assert.EndsWith("…", conversation.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Fake_rewrite_carries_topic_from_earlier_turns()
    {
        const string request = "Conversation:\nUser: How do I reset my password?\nAssistant: Open Settings.\n\n<follow_up>\nand on mobile?\n</follow_up>";

        Assert.Equal("How do I reset my password? and on mobile?", FakeChatClient.Rewrite(request));
    }

    [Fact]
    public void Rewrite_prompt_exists_and_forbids_answering()
    {
        var prompt = PromptLibrary.Get("query-rewrite.v1");

        Assert.Contains("standalone question", prompt, StringComparison.Ordinal);
        Assert.Contains("Do not answer", prompt, StringComparison.Ordinal);
    }
}
