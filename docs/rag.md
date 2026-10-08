# RAG Pipeline

Retrieval-Augmented Generation answers questions from the tenant's own documents instead of the
model's training data. It has two halves: **ingestion** (offline, per document) and **retrieval +
generation** (online, per question).

## 1. Ingestion (implemented)

```
Upload ──► documents (status = Uploaded) + file storage
              │
              ▼   DocumentIngestionWorker (background, in-process)
        claim: SELECT … FOR UPDATE SKIP LOCKED      ──► status = Processing (lease starts)
              │
        extract   PDF: PdfPig, one section per page
                  DOCX: OpenXML, heading styles → "## "
                  TXT/MD: UTF-8 decode
              │
        normalize NFKC, newlines, control chars, PDF hyphenation, whitespace
              │
        chunk     paragraphs → sentences → words, packed to 512 tokens, 64-token overlap
              │
        store     document_chunks (replaces previous chunks in one transaction)
              ▼
        status = Processed (chunk_count)  |  Failed (safe error message)
```

### Why a Postgres-backed queue

The document row *is* the job. `FOR UPDATE SKIP LOCKED` lets any number of workers (one per API
instance) claim different documents without coordination. No extra infrastructure, no dual-write
problem between a database and a broker. A broker (Azure Service Bus) becomes worthwhile when
ingestion moves into its own service or throughput needs exceed polling.

### Reliability

| Concern | Mechanism |
|---|---|
| Worker crashes mid-document | Lease: a `Processing` row with `updated_at` older than `Ingestion:Lease` (10 min) is re-claimed |
| Transient failure (storage, DB) | Released back to `Uploaded`; retried until `MaxAttempts` (3) |
| Poison document | After `MaxAttempts`, marked `Failed` instead of retrying forever |
| Bad file (corrupt, encrypted, image-only) | `DocumentExtractionException` → `Failed` immediately with an actionable message |
| Re-running a document | Idempotent: chunks are replaced in the same transaction that marks it `Processed` |
| Retry / re-index | `POST /api/documents/{id}/reprocess` (Admin) |
| Decompression bombs | Extracted text capped at `Ingestion:MaxExtractedCharacters` |

### Chunking choices

- **Token-based, not character-based.** Embedding models limit and bill by tokens. Counting uses
  `cl100k_base`, the encoding of OpenAI's `text-embedding-3` models.
- **~512 tokens.** Small enough that a chunk is about one topic (precise retrieval) and several
  fit in a prompt; large enough to carry the context needed to answer. Tunable via config.
- **Structure-aware splitting.** Breaking on paragraphs, then sentences, keeps chunks coherent;
  arbitrary fixed-size windows cut facts in half and produce noisier embeddings.
- **Overlap (64 tokens).** A fact straddling a boundary still appears whole in one chunk.
- **Metadata for citations.** PDF chunks never span pages, so each carries one `page_number`;
  Markdown/DOCX chunks carry their section `heading`.

### Known limitations

- No OCR: scanned PDFs fail with a clear message.
- PDF tables and multi-column layouts are extracted in reading order but lose structure.
- Headings are detected for Markdown and DOCX styles, not inferred from PDF font sizes.

## 2. Embeddings and vector search

_Phase 5._

## 3. Retrieval and answer generation

_Phase 6–7._
