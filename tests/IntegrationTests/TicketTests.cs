using System.Net;
using System.Net.Http.Json;
using AISupportOps.Application.Documents;
using AISupportOps.Application.Identity;
using AISupportOps.Application.Tickets;
using AISupportOps.Domain.Auditing;
using AISupportOps.Domain.Tenants;
using AISupportOps.Domain.Tickets;
using AISupportOps.IntegrationTests.Fixtures;
using static AISupportOps.IntegrationTests.Fixtures.TestApi;

namespace AISupportOps.IntegrationTests;

[Collection(ApiCollection.Name)]
public class TicketTests(ApiFactory factory)
{
    [Fact]
    public async Task Ticket_numbers_are_sequential_per_tenant_and_independent_across_tenants()
    {
        using var a = factory.CreateClient(await NewTenantAsync());
        using var b = factory.CreateClient(await NewTenantAsync());

        var a1 = await CreateAsync(a, "A first");
        var a2 = await CreateAsync(a, "A second");
        var b1 = await CreateAsync(b, "B first");

        Assert.Equal((1, 2, 1), (a1.Number, a2.Number, b1.Number));
        Assert.Equal(TicketStatus.Open, a1.Status);
        Assert.Equal(TicketSource.Manual, a1.Source);
    }

    [Fact]
    public async Task Concurrent_creation_never_duplicates_ticket_numbers()
    {
        var owner = await NewTenantAsync();

        var created = await Task.WhenAll(Enumerable.Range(1, 20).Select(async i =>
        {
            using var client = factory.CreateClient(owner);
            return await CreateAsync(client, $"Parallel {i}");
        }));

        Assert.Equal(Enumerable.Range(1, 20), created.Select(t => t.Number).Order());
    }

    [Fact]
    public async Task Ticket_with_customer_and_assignee_is_returned_with_references()
    {
        var owner = await NewTenantAsync();
        var agent = await factory.AddMemberAsync(owner, TenantRole.Agent);
        var agentId = await factory.UserIdOfAsync(agent);
        using var client = factory.CreateClient(owner);
        var customer = await CreateCustomerAsync(client, "Jane Doe", UniqueEmail("jane"));

        var ticket = await CreateAsync(client, "Invoice is wrong", customer.Id, agentId, TicketPriority.High);

        Assert.Equal(("Jane Doe", "New Agent"), (ticket.Customer!.Name, ticket.Assignee!.DisplayName));
        Assert.Equal(TicketPriority.High, ticket.Priority);
        Assert.Equal([TicketStatus.InProgress, TicketStatus.Resolved, TicketStatus.Closed], ticket.AllowedNextStatuses);
    }

    [Fact]
    public async Task Workflow_updates_are_validated_and_fully_audited()
    {
        var owner = await NewTenantAsync();
        using var client = factory.CreateClient(owner);
        var ticket = await CreateAsync(client, "Cannot log in");

        var inProgress = await UpdateAsync(client, ticket.Id, new UpdateTicketRequest(Status: TicketStatus.InProgress, Priority: TicketPriority.Critical));
        var invalid = await client.PatchAsJsonAsync($"/api/tickets/{ticket.Id}", new UpdateTicketRequest(Status: TicketStatus.Closed), Json);
        var resolved = await UpdateAsync(client, ticket.Id, new UpdateTicketRequest(Status: TicketStatus.Resolved));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Contains("cannot move from InProgress to Closed", await invalid.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(TicketPriority.Critical, inProgress.Priority);
        Assert.NotNull(resolved.ResolvedAt);

        var history = await (await client.GetAsync(new Uri($"/api/tickets/{ticket.Id}/history", UriKind.Relative)))
            .ReadAsync<List<AuditEntryResponse>>(HttpStatusCode.OK);
        Assert.Equal(["ticket.created", "ticket.updated", "ticket.updated"], history.Select(h => h.Action));
        var update = history[1];
        Assert.Equal(AuditActorType.User, update.ActorType);
        Assert.Equal("Test User", update.ActorName);
        Assert.Contains(update.Changes, c => c is { Field: "status", From: "Open", To: "InProgress" });
        Assert.Contains(update.Changes, c => c is { Field: "priority", From: "Medium", To: "Critical" });
        Assert.NotNull(update.CorrelationId);
    }

    [Fact]
    public async Task Stale_version_is_rejected_to_prevent_lost_updates()
    {
        using var client = factory.CreateClient(await NewTenantAsync());
        var ticket = await CreateAsync(client, "Concurrency");

        // Agent 1 updates using the version they loaded…
        await UpdateAsync(client, ticket.Id, new UpdateTicketRequest(Priority: TicketPriority.Low, ExpectedVersion: ticket.Version));
        // …agent 2 still holds the old version.
        var stale = await client.PatchAsJsonAsync($"/api/tickets/{ticket.Id}", new UpdateTicketRequest(Priority: TicketPriority.High, ExpectedVersion: ticket.Version), Json);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(TicketPriority.Low, (await GetAsync(client, ticket.Id)).Priority);
    }

    [Fact]
    public async Task Assignee_must_be_an_agent_or_higher_in_the_same_tenant()
    {
        var owner = await NewTenantAsync();
        var viewer = await factory.AddMemberAsync(owner, TenantRole.Viewer);
        var outsider = await NewTenantAsync();
        using var client = factory.CreateClient(owner);
        var ticket = await CreateAsync(client, "Assign me");

        var toViewer = await client.PatchAsJsonAsync($"/api/tickets/{ticket.Id}", new UpdateTicketRequest(AssigneeUserId: await factory.UserIdOfAsync(viewer)), Json);
        var toOutsider = await client.PatchAsJsonAsync($"/api/tickets/{ticket.Id}", new UpdateTicketRequest(AssigneeUserId: await factory.UserIdOfAsync(outsider)), Json);
        var toSelf = await UpdateAsync(client, ticket.Id, new UpdateTicketRequest(AssigneeUserId: await factory.UserIdOfAsync(owner)));
        var unassigned = await UpdateAsync(client, ticket.Id, new UpdateTicketRequest(Unassign: true));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, toViewer.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, toOutsider.StatusCode);
        Assert.NotNull(toSelf.Assignee);
        Assert.Null(unassigned.Assignee);
    }

    [Fact]
    public async Task Viewers_can_read_but_not_create_or_update()
    {
        var owner = await NewTenantAsync();
        using var ownerClient = factory.CreateClient(owner);
        var ticket = await CreateAsync(ownerClient, "Read only");
        using var viewer = factory.CreateClient(await factory.AddMemberAsync(owner, TenantRole.Viewer));

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(new Uri($"/api/tickets/{ticket.Id}", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/tickets", new CreateTicketRequest("x"), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PatchAsJsonAsync($"/api/tickets/{ticket.Id}", new UpdateTicketRequest(Title: "y"), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(new Uri("/api/audit-logs", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task Tickets_customers_and_audit_logs_are_tenant_isolated()
    {
        using var a = factory.CreateClient(await NewTenantAsync());
        using var b = factory.CreateClient(await NewTenantAsync());
        var customer = await CreateCustomerAsync(a, "Private Co", UniqueEmail("private"));
        var ticket = await CreateAsync(a, "Tenant A ticket", customer.Id);

        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync(new Uri($"/api/tickets/{ticket.Id}", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync(new Uri("/api/tickets/number/1", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PatchAsJsonAsync($"/api/tickets/{ticket.Id}", new UpdateTicketRequest(Title: "pwned"), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync(new Uri($"/api/customers/{customer.Id}", UriKind.Relative))).StatusCode);
        // B cannot attach A's customer to its own ticket.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await b.PostAsJsonAsync("/api/tickets", new CreateTicketRequest("x", CustomerId: customer.Id), Json)).StatusCode);

        var bAudit = await (await b.GetAsync(new Uri("/api/audit-logs", UriKind.Relative))).ReadAsync<PagedResponse<AuditEntryResponse>>(HttpStatusCode.OK);
        Assert.DoesNotContain(bAudit.Items, e => e.EntityId == ticket.Id || e.EntityId == customer.Id);
    }

    [Fact]
    public async Task List_filters_by_status_assignee_and_search()
    {
        var owner = await NewTenantAsync();
        using var client = factory.CreateClient(owner);
        var ownerId = await factory.UserIdOfAsync(owner);
        var login = await CreateAsync(client, "Login page broken", assigneeId: ownerId);
        await CreateAsync(client, "Billing question");
        await UpdateAsync(client, login.Id, new UpdateTicketRequest(Status: TicketStatus.InProgress));

        var mine = await ListAsync(client, "assignee=me");
        var unassigned = await ListAsync(client, "assignee=unassigned");
        var inProgress = await ListAsync(client, "status=InProgress");
        var search = await ListAsync(client, "search=LOGIN");
        var byNumber = await ListAsync(client, "search=%232"); // "#2"

        Assert.Equal(["Login page broken"], mine.Items.Select(t => t.Title));
        Assert.Equal(["Billing question"], unassigned.Items.Select(t => t.Title));
        Assert.Equal(["Login page broken"], inProgress.Items.Select(t => t.Title));
        Assert.Equal(["Login page broken"], search.Items.Select(t => t.Title));
        Assert.Equal(["Billing question"], byNumber.Items.Select(t => t.Title));
    }

    [Fact]
    public async Task Customer_email_is_unique_per_tenant_and_detail_includes_tickets()
    {
        using var a = factory.CreateClient(await NewTenantAsync());
        using var b = factory.CreateClient(await NewTenantAsync());
        var email = UniqueEmail("dup");
        var customer = await CreateCustomerAsync(a, "Dup", email);
        await CreateAsync(a, "Their ticket", customer.Id);

        var duplicate = await a.PostAsJsonAsync("/api/customers", new CreateCustomerRequest("Dup 2", email.ToUpperInvariant()), Json);
        var otherTenant = await b.PostAsJsonAsync("/api/customers", new CreateCustomerRequest("Dup", email), Json);
        var detail = await (await a.GetAsync(new Uri($"/api/customers/{customer.Id}", UriKind.Relative))).ReadAsync<CustomerDetail>(HttpStatusCode.OK);

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.Created, otherTenant.StatusCode);
        Assert.Equal(1, detail.OpenTicketCount);
        Assert.Equal("Their ticket", Assert.Single(detail.RecentTickets).Title);
    }

    // ---- helpers ----

    private async Task<AuthResponse> NewTenantAsync()
    {
        using var client = factory.CreateClient();
        return await client.RegisterAsync();
    }

    private static async Task<TicketResponse> CreateAsync(HttpClient client, string title, Guid? customerId = null, Guid? assigneeId = null, TicketPriority priority = TicketPriority.Medium) =>
        await (await client.PostAsJsonAsync("/api/tickets", new CreateTicketRequest(title, "details", priority, customerId, assigneeId), Json))
            .ReadAsync<TicketResponse>(HttpStatusCode.Created);

    private static async Task<TicketResponse> UpdateAsync(HttpClient client, Guid id, UpdateTicketRequest request) =>
        await (await client.PatchAsJsonAsync($"/api/tickets/{id}", request, Json)).ReadAsync<TicketResponse>(HttpStatusCode.OK);

    private static async Task<TicketResponse> GetAsync(HttpClient client, Guid id) =>
        await (await client.GetAsync(new Uri($"/api/tickets/{id}", UriKind.Relative))).ReadAsync<TicketResponse>(HttpStatusCode.OK);

    private static async Task<PagedResponse<TicketResponse>> ListAsync(HttpClient client, string query) =>
        await (await client.GetAsync(new Uri($"/api/tickets?{query}", UriKind.Relative))).ReadAsync<PagedResponse<TicketResponse>>(HttpStatusCode.OK);

    private static async Task<CustomerResponse> CreateCustomerAsync(HttpClient client, string name, string email) =>
        await (await client.PostAsJsonAsync("/api/customers", new CreateCustomerRequest(name, email), Json)).ReadAsync<CustomerResponse>(HttpStatusCode.Created);
}
