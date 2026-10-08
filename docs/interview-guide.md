# Interview guide

These are short answers you can say out loud in an interview. Each one starts with the decision, then gives the reason, then points to the code or document in this repo that shows it.

## 1. Why a modular monolith?

It gives one deployable, one database transaction and one debugger session. Clear module boundaries still keep the code organized:

- Domain, Application, Infrastructure and Api layers;
- modules such as Documents, Knowledge, Chat, Tickets and Agents;
- architecture tests that fail the build if the layering is broken.

Microservices would add network calls, distributed transactions and deployment overhead that a small team and this scale don't need. Because the boundaries are already in place, a module can be extracted later once it has a real reason to scale on its own. See [architecture.md](architecture.md).

## 2. Why PostgreSQL?

The data is relational: tenants, memberships, tickets, an audit trail. PostgreSQL also gives us everything else we need in one engine:

- transactions, so an audit entry commits with its change;
- `xmin` for optimistic concurrency;
- `FOR UPDATE SKIP LOCKED`, which turns a table into a job queue;
- JSONB;
- vectors, through pgvector.

One database means one backup, one security model and one thing to operate.

## 3. Why pgvector instead of a dedicated vector database?

The vectors live next to the rows they describe. A chunk delete cascades with its document, and the tenant filter is an ordinary `WHERE` clause in the same query. There is no second system to keep in sync and no dual-write consistency problem.

HNSW indexes give approximate nearest-neighbour search with good recall into the millions of vectors, which covers per-tenant knowledge bases comfortably. I would revisit the choice at hundreds of millions of vectors or if filtered-search performance became the bottleneck.

## 4. How does RAG work here?

RAG means "look it up, then answer from what you found." For each question:

1. **Rewrite.** A follow-up question is rewritten into a standalone one.
2. **Embed.** The question is embedded, with a Redis cache in front.
3. **Search.** The tenant's chunks are searched by cosine similarity.
4. **Threshold.** Chunks below a relevance threshold are dropped. If nothing remains, the system abstains and never calls the LLM.
5. **Build context.** The rest form numbered, escaped sources within a token budget.
6. **Answer.** A versioned prompt tells the model to answer only from those sources and to cite them as `[n]`.
7. **Verify.** Citations that don't match a provided source are removed.
8. **Stream.** The answer streams to the browser and is stored with its citations, tokens and latency.

See [rag.md](rag.md).

## 5. Why chunk documents?

Embeddings work best on focused passages, and the prompt has a token budget. If you embed a whole manual, its vector becomes an average of every topic in it, so it matches nothing well.

Chunks follow the document's structure. Each heading starts a new chunk, so one chunk equals one topic. Chunks are at most 512 tokens, with 64 tokens of overlap so a sentence at a boundary isn't lost. Each chunk is embedded with a short header (file name and heading), so the chunk still carries its context.

## 6. How are embeddings generated?

The ingestion worker sends batches of chunk texts to `text-embedding-3-small` through `IEmbeddingGenerator` from Microsoft.Extensions.AI. Each chunk gets back a 1536-dimension vector, which is stored in `vector(1536)`. Questions are embedded with the same model at query time, because vectors from different models aren't comparable.

The provider sits behind an interface. Tests use a deterministic Fake embedder, so they need no API key.

## 7. How does vector similarity search work?

Text with similar meaning produces vectors that point in similar directions. Cosine distance measures the angle between two vectors.

An HNSW index is a layered graph of neighbouring vectors. A search walks that graph to find approximate nearest neighbours in roughly logarithmic time, instead of scanning every row. The query is: `WHERE tenant_id = @tenant ORDER BY embedding <=> @query LIMIT k`. Iterative index scans keep filtered queries from returning too few rows. The resulting similarity score is what the abstention threshold uses.

## 8. How does tenant isolation work?

There are several layers:

- **The token.** The tenant comes from the JWT, never from the request body. Membership is checked against the database.
- **Reads.** EF Core global query filters add `tenant_id = current` to every tenant-owned entity.
- **Writes.** A `SaveChanges` guard rejects entities whose tenant doesn't match.
- **Raw SQL.** The vector search query has an explicit tenant predicate.
- **Cache.** Redis keys include the tenant id.
- **Tests.** Integration tests sign in as tenant A, try to read and modify tenant B's data, and must get a 404.

The next layer I would add is Postgres row-level security.

## 9. Why Redis?

Redis holds state that every API instance has to share and that has to be fast:

- **Rate limits.** Fixed-window counters, per IP for auth and per user for AI calls.
- **Account lockout.** An atomic Lua script prevents parallel password guesses from slipping past the limit.
- **Embedding cache.** It saves an API call and latency on repeated questions.

Durable data stays in Postgres. Redis can be flushed without losing anything important.

## 10. How does streaming work?

The chat endpoint uses Server-Sent Events: one long HTTP response with `text/event-stream`. It sends a `meta` event (conversation id and sources), then many `delta` events (pieces of text), then `done` or `error`.

- **Browser.** It reads the stream with `fetch` and a small parser, because `EventSource` can't send POST requests or auth headers.
- **nginx.** Buffering is turned off so tokens arrive immediately.
- **Persistence.** The message is saved as Completed, Interrupted (client disconnected) or Failed, so a partial answer is never lost silently.

## 11. How does agentic AI work?

The agent is a loop:

1. Send the conversation and the list of available tools to the model.
2. If the model answers in text, stop.
3. If it asks for tools, run each call through `ToolExecutor`, append the results, and go back to step 1.

The loop stops at 6 iterations, and each run is limited to 10 tool calls and 3 writes. Those budgets cap cost, latency and the damage a confused or manipulated model can do. See [agentic-ai.md](agentic-ai.md).

## 12. How does tool calling work?

Each tool is a typed C# record of arguments plus a handler. A JSON schema is generated from the record and sent to the model as a **declaration only**. The model replies with a tool name and JSON arguments. Our code then deserializes the arguments strictly (unknown fields are rejected), validates them, and runs the handler. The handler calls the same application services the REST API uses. The result goes back to the model as a tool message.

## 13. How do you prevent unauthorized tool execution?

The model is treated as untrusted input:

- **Offered tools.** A Viewer is only offered read tools.
- **Every call.** `ToolExecutor` re-reads the user's role from the database, checks it against the tool's minimum role, and checks the budget and the argument schema.
- **Data access.** Handlers run inside the user's tenant scope, with the same EF filters as the API.
- **Records.** Every call is recorded in `tool_executions` and the audit log with actor type `AiAgent`, whether it succeeded or was rejected.

So even a successful prompt injection can only ask for something the user was already allowed to do.

## 14. How do you handle hallucinations?

- **Retrieve first.** No answer is generated without relevant context, because the relevance threshold triggers abstention.
- **Constrain the prompt.** It says "answer only from the sources and cite them" and gives an explicit way to say "I don't know."
- **Lower the randomness.** Temperature is 0.1.
- **Verify citations.** A citation that doesn't point to a provided source is stripped.
- **Measure.** The evaluation suite includes groundedness scored by an LLM judge, and the dashboard tracks user feedback.

Hallucination can't be eliminated, but it can be made rarer, visible and measurable.

## 15. How do you evaluate RAG quality?

I use a labelled dataset of questions, each with an expected source document and key facts, plus questions that should be refused. I measure retrieval and generation separately:

| Stage | Metric |
|---|---|
| Retrieval | Hit@K and MRR |
| Answer | Citation accuracy, key-fact coverage, abstention accuracy |
| Grounding | An LLM judge checks that every claim is supported by the context |

The dataset runs in CI as a quality gate, so a chunking or prompt change that hurts quality fails the build. In production, the dashboard tracks abstention rate, citation rate, latency, tokens and thumbs up/down. See [evaluation.md](evaluation.md).

## 16. How do you handle prompt injection?

Assume it will sometimes succeed, and limit what it can do:

- **Mark untrusted text.** Retrieved document text is escaped and wrapped as data, and the system prompt says instructions inside sources must be ignored.
- **Separate prompts.** User input and system instructions are never concatenated into one string.
- **Bound the damage.** The real protection is that the model has no authority of its own. Tools are authorized against the user's role, validated and budgeted, data is tenant-scoped, and every action is audited.
- **Safe rendering.** Model output is rendered as Markdown without raw HTML, and external links open with `noopener`.

## 17. How does authentication work?

- **Login.** It returns a 15-minute JWT access token, which carries the user and the tenant.
- **Refresh tokens.** A rotating refresh token sits in an httpOnly, `SameSite=Strict` cookie, which JavaScript can't read.
- **Rotation and reuse.** Each refresh issues a new token and revokes the old one. If an already-used token appears again, the whole token family is revoked, because that means it was stolen.
- **CSRF.** Requests that use the cookie need a custom header, which a cross-site form can't send.
- **Brute force.** Passwords are hashed. Failed logins trigger an atomic Redis lockout, and the auth endpoints are rate limited per IP.

## 18. How would you scale the system?

- **API.** It is stateless (JWT, Redis-backed limits), so run more replicas behind the load balancer. Container Apps autoscales on HTTP load.
- **Ingestion.** Workers scale with the replicas, because `SKIP LOCKED` lets them share the queue safely. If ingestion needs different scaling, split it into its own container app.
- **Database.** Add read replicas for dashboards and search, tune or partition the HNSW index, add PgBouncer for connections, and partition large tables by tenant.
- **AI calls.** Add caching, request quotas and a provider fallback.

## 19. What would you turn into microservices later?

Only modules with a different scaling or failure profile:

1. **Ingestion.** It is CPU and I/O heavy and bursty, so it already behaves like a worker.
2. **The AI gateway.** Model calls, quotas, caching and provider routing in one place.
3. **Evaluation.** Batch jobs that shouldn't compete with interactive traffic.

Identity, tickets and chat would stay together, because they share transactions. The trigger to split is a measured need, not the architecture diagram.

## 20. How would you reduce AI cost?

- **Measure first.** Tokens are already logged per message and run.
- **Skip calls.** Abstaining below the threshold skips the LLM call, and the Redis embedding cache skips repeat embeddings.
- **Smaller prompts.** Context is budgeted at 3,000 tokens, and long histories are summarized.
- **Next steps:**
  - a semantic answer cache for frequent questions;
  - routing easy questions and query rewriting to a smaller model;
  - batch APIs for evaluation and ingestion;
  - per-tenant quotas, enforced with the same Redis limiter.

## 21. How would you improve retrieval quality?

Change one thing at a time and let the evaluation suite decide. In order:

1. **Hybrid search.** Combine Postgres full-text search with vectors using reciprocal rank fusion, which helps with exact terms such as error codes.
2. **Reranking.** Rerank the top 20 with a cross-encoder.
3. **Threshold calibration.** Tune the threshold per embedding model, using the abstention metrics.
4. **Better chunks.** Improve tables and PDF layout, and add document metadata as filters.
5. **Query expansion.** Expand queries for vague questions.

## 22. How would you deploy this to Azure?

The repo already contains the Bicep template and the workflow:

- **Hosting.** Azure Container Apps runs the web and API apps, plus a job for migrations.
- **Data.** PostgreSQL Flexible Server with the `vector` extension, and Azure Managed Redis.
- **Storage and secrets.** Blob Storage through managed identity, and Key Vault for secrets.
- **Monitoring and network.** Application Insights, and a VNet that keeps the data services private.

The deploy workflow logs in with GitHub OIDC (no stored credentials). It pulls the SHA-tagged image from GHCR, runs the migration job, rolls out new revisions, and smoke-tests `/health/ready`.

To be honest about the status: it's written and documented but not yet deployed to a live subscription. See [deployment.md](deployment.md).
