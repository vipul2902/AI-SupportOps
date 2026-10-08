# AI Evaluation

Two complementary views of quality:

| | Offline evaluation | Online metrics |
|---|---|---|
| Question | "Is this configuration good *before* we ship it?" | "How is production behaving *now*?" |
| Data | Labelled dataset (`evaluation/dataset.json`) | Every assistant message and tool call already stored |
| When | On demand and in CI (EvaluationTests) | Continuously |
| API | `POST /api/evaluations/runs`, `GET /api/evaluations/runs[/{id}]` | `GET /api/evaluations/metrics?days=30` |

All evaluation endpoints are Admin-only. Users rate answers with
`POST /api/conversations/{id}/messages/{messageId}/feedback` `{ "helpful": true|false, "comment"? }`.

## Offline evaluation

A dataset case:

```json
{ "id": "pw-link-expiry",
  "question": "How long until the password reset link expires?",
  "expectedSource": "account-security.md",
  "keyFacts": ["30 minutes"] }
{ "id": "no-student-discount", "question": "Is there a student discount?", "shouldAbstain": true }
```

Each case runs through the **real pipeline**: the same retrieval, threshold, prompt version, and
models as production. The run records which prompt and models were used, so runs are comparable.

### Metrics

| Metric | Measures | How | Kind |
|---|---|---|---|
| Hit@K | Retrieval recall | Expected document among the chunks that reached the prompt | Deterministic |
| MRR | Retrieval ranking | Mean of 1/rank of the expected document | Deterministic |
| Answer rate | Coverage | Answerable cases that were answered | Deterministic |
| Citation accuracy | Attribution | Answered cases citing the expected document | Deterministic |
| Key-fact coverage | Correctness (proxy) | Share of required facts present in the answer | Deterministic |
| Abstention accuracy | Calibration / hallucination | Abstained exactly when the case says it should | Deterministic |
| Groundedness | Faithfulness | LLM judge (`eval-groundedness.v1`): are all claims supported by the sources? | **Model-judged** |
| Latency, tokens | Cost and speed | Measured per case | Deterministic |

Deterministic checks come first because they are cheap, repeatable, and explainable. The judge adds a
signal for what string checks cannot see (claims beyond the sources), at the cost of certainty.

### The regression gate

`tests/EvaluationTests` uploads `evaluation/docs`, runs the dataset with the offline Fake providers,
and fails the build if quality drops: Hit@K ≥ 0.9, MRR ≥ 0.8, citation accuracy ≥ 0.9, key-fact
coverage ≥ 0.7, groundedness ≥ 0.8, and **every unanswerable case must abstain**.

**It already paid off.** The first run scored Hit@K 0.7 and abstention accuracy 0.69. Diagnosis:
small multi-section documents became one chunk, diluting the embedding, so short questions about a
single section fell below the relevance threshold. Fix: the chunker now starts a new chunk at each
heading. Result: Hit@K 0.9, abstention accuracy 0.92, verified by the same suite.

One case (`webhook-signature`) still misses offline: the question says "requests … signed", the
document "request is signed", and the offline embedder matches exact words only. It is kept in the
dataset deliberately rather than reworded to pass.

### Running with real models

Set `Ai:Provider=OpenAI` and an API key, upload `evaluation/docs`, and `POST` the dataset to
`/api/evaluations/runs` with `"useLlmJudge": true`. Compare runs after every prompt, chunking,
threshold, or model change. Offline Fake scores verify pipeline mechanics; they say nothing about
real model quality.

## Online metrics

`GET /api/evaluations/metrics` aggregates the last N days of production data:

- **Volume and outcomes:** answers by outcome (`Answered`, `Uncited`, `Declined`, `NoRelevantSources`, `Agent*`).
- **Answer rate, abstention rate, uncited rate:** a rising uncited rate is an early hallucination signal.
- **Retrieval confidence:** average citations per answer, average top citation score.
- **Latency:** average, p50, p95 (nearest rank). Tail latency matters more than the mean for UX.
- **Cost:** input and output tokens.
- **User satisfaction:** helpful vs. not helpful feedback.
- **Agent tool health:** calls, successes, denials, invalid arguments, failures, latency per tool. Spikes in denials or invalid arguments flag prompt or model problems, or probing.
- **Trend:** daily questions, answers, and average latency; plus the latest offline evaluation summary.

## Limitations: read before trusting the numbers

- **No metric proves the absence of hallucination.** Citation checks prove a cited source was *provided*,
  not that it *supports* the sentence. Key facts are substring matches: they miss paraphrases and can
  pass a wrong answer that happens to contain the string.
- **LLM judges are models.** They can be lenient, inconsistent between runs, biased toward fluent or
  long answers, and fooled by the same errors as the generator. Use them for trends and triage, then
  spot-check by hand. Pin the judge model and prompt version when comparing runs.
- **Small datasets are noisy.** One case in 13 moves a rate by ~8 points. Grow the dataset from real
  user questions, especially ones with negative feedback, and keep it versioned.
- **The dataset can overfit.** Tuning to pass specific cases (rather than fixing root causes) inflates
  scores without improving real quality, which is why the failing offline case above was not reworded.
- **Online metrics are proxies.** Thumbs-down is sparse and biased toward strong reactions; a high
  answer rate is bad if answers are wrong. Read metrics together, not in isolation.
- **Scale.** Metrics aggregate up to 20,000 recent answers in memory, and evaluation runs synchronously
  in the request. Fine at this size; at scale, pre-aggregate and run evaluations in the background worker.
