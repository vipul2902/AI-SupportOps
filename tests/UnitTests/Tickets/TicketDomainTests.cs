using AISupportOps.Application.Auditing;
using AISupportOps.Domain.Tickets;
using static AISupportOps.Domain.Tickets.TicketStatus;

namespace AISupportOps.UnitTests.Tickets;

public class TicketDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private static SupportTicket NewTicket() =>
        new(Guid.NewGuid(), 1, "  Cannot log in  ", "desc", TicketPriority.High, null, Guid.NewGuid(), TicketSource.Manual);

    [Theory]
    [InlineData(Open, InProgress, true)]
    [InlineData(Open, Resolved, true)]
    [InlineData(Open, Closed, true)]
    [InlineData(InProgress, Resolved, true)]
    [InlineData(InProgress, Closed, false)] // must be resolved first
    [InlineData(Resolved, InProgress, true)] // reopen
    [InlineData(Resolved, Closed, true)]
    [InlineData(Resolved, Open, false)]
    [InlineData(Closed, Open, true)] // reopen
    [InlineData(Closed, InProgress, false)]
    [InlineData(Closed, Closed, true)] // no-op
    public void Status_transitions_follow_the_workflow(TicketStatus from, TicketStatus to, bool allowed) =>
        Assert.Equal(allowed, SupportTicket.CanTransition(from, to));

    [Fact]
    public void New_ticket_is_open_with_trimmed_title()
    {
        var ticket = NewTicket();

        Assert.Equal(Open, ticket.Status);
        Assert.Equal("Cannot log in", ticket.Title);
    }

    [Fact]
    public void Resolving_and_closing_set_timestamps_and_reopening_clears_them()
    {
        var ticket = NewTicket();

        ticket.ChangeStatus(Resolved, Now);
        ticket.ChangeStatus(Closed, Now.AddHours(1));
        Assert.Equal((Now, Now.AddHours(1)), (ticket.ResolvedAt!.Value, ticket.ClosedAt!.Value));

        ticket.ChangeStatus(Open, Now.AddHours(2));
        Assert.Null(ticket.ResolvedAt);
        Assert.Null(ticket.ClosedAt);
    }

    [Fact]
    public void Invalid_transition_throws_with_allowed_options()
    {
        var ticket = NewTicket();
        ticket.ChangeStatus(InProgress, Now);

        var ex = Assert.Throws<InvalidTicketTransitionException>(() => ticket.ChangeStatus(Closed, Now));

        Assert.Contains("Allowed: Open, Resolved", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangeSet_records_only_real_changes_with_invariant_formatting()
    {
        var changes = new ChangeSet()
            .Track("title", "A", "A")
            .Track("priority", TicketPriority.Low, TicketPriority.Critical)
            .Track("assigneeUserId", (Guid?)null, (Guid?)Guid.Empty)
            .Track("score", 1.5, 2.5);

        Assert.Equal(["priority", "assigneeUserId", "score"], changes.Changes.Select(c => c.Field));
        Assert.Equal(("Low", "Critical"), (changes.Changes[0].From, changes.Changes[0].To));
        Assert.Equal("2.5", changes.Changes[2].To);
    }
}
