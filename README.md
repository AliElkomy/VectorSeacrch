# VectorSearch — RAG Document API

An ASP.NET Core 9 Web API implementing Retrieval-Augmented Generation (RAG) over uploaded documents. It
extracts text from PDF/DOCX/TXT, splits it into chunks, generates embeddings with a local Ollama model,
stores the vectors in a **SQL Server 2025 `VECTOR` column**, and answers natural-language questions using
`VECTOR_DISTANCE` cosine similarity plus a local chat model.

---

## Tech Stack

| Concern | Choice | Version |
| --- | --- | --- |
| Framework | ASP.NET Core Web API | net9.0 |
| SDK | .NET | 10.0.401 |
| ORM | EF Core (SqlServer) | 9.0.20 |
| EF CLI | dotnet-ef | 10.0.8 |
| Database | SQL Server 2025 (native `VECTOR` type) | 17.0.1000.7 |
| AI abstractions | `Microsoft.Extensions.AI` | 10.10.0 |
| Local inference | OllamaSharp → Ollama daemon | 5.4.30 |
| Embedding model | `qwen3-embedding:0.6b` — **1024 dims**, 32k ctx | — |
| Chat model | `qwen2.5:1.5b` | — |
| PDF parsing | UglyToad.PdfPig | 1.7.0-custom-5 |
| DOCX parsing | DocumentFormat.OpenXml | 3.5.1 |
| API docs | Swashbuckle (Swagger UI / OpenAPI) | 7.2.0 |

Models currently pulled in Ollama: `qwen2.5:1.5b`, `llama3.2:latest`, `qwen3-embedding:0.6b`,
`qwen2.5-coder:7b`, `all-minilm:latest`.

---

## Architecture

Two flows: ingestion builds the index, retrieval queries it.

### Ingestion — `POST /api/Documents/upload`

```
IFormFile
   │
   ├─ 1. TEXT EXTRACTION   .txt  → StreamReader.ReadToEndAsync()   (async)
   │                       .pdf  → ExtractPdfText   (PdfPig, sync)
   │                       .docx → ExtractDocxText  (OpenXml, sync)
   │                       other → NotSupportedException
   │
   ├─ 2. PERSIST HEADER    INSERT Documents → identity generates DocumentID
   │
   ├─ 3. CHUNK             whitespace split, fixed 250-word windows, no overlap
   │                       documents over 20,000 chunks are rejected with 413
   │
   └─ 4. EMBED + PERSIST   chunks are embedded in batches of 32:
                              GenerateAsync(batch, HttpContext.RequestAborted)
                              → vector serialized to JSON
                              → INSERT DocumentChunks
                              → SaveChangesAsync() per batch, with a progress log line
```

Each embedding request is bounded by the client's cancellation token, and the write happens incrementally
so a failure partway through leaves only the batches already completed.

### Retrieval + Generation — `POST /api/Documents/chat`

```
Question
   │
   ├─ 1. QUERY EMBEDDING   question → vector (single input)
   │
   ├─ 2. VECTOR SEARCH     raw SQL via FromSqlInterpolated:
   │                        SELECT TOP (3) ChunkID, DocumentID, ChunkContent, Embedding
   │                        FROM DocumentChunks
   │                        ORDER BY VECTOR_DISTANCE('cosine', Embedding, CAST(@p AS VECTOR(1024))) ASC
   │
   ├─ 3. PROMPT            system prompt restricts the model to retrieved context;
   │                       chunks joined with a "\n---\n" separator
   │
   └─ 4. GENERATION        IChatClient.GetResponseAsync → answer + sources + total timing
```

Notes on this flow:

- The search is **raw SQL** because `VECTOR_DISTANCE` has no EF LINQ translation. `FromSqlInterpolated`
  (not `FromSqlRaw`) is used so the query vector travels as a **parameter**, not concatenated SQL.
- `CAST(@p AS VECTOR(1024))` tells SQL Server the parameter's dimension. It **must** equal the column's
  declared dimension, or the query fails at runtime with a dimension mismatch.
- The system prompt is a guardrail worth keeping intact:
  > Answer the question using ONLY the provided document context. If the context does not contain enough
  > information, state that clearly.
- `.Select(c => c.ChunkContent)` projects **only** the text, so the response has no way to say which
  document a chunk came from (see [Known Issues](#known-issues)).

---

## Project Structure

```
VectorSeacrch/
├── Program.cs                             # DI, DB, AI clients, auto-migrate, Swagger
├── VectorSeacrch.csproj                   # net9.0 + package refs
├── VectorSeacrch.slnx                     # single-project solution
├── VectorSeacrch.csproj.Backup.tmp        # VS backup artifact
├── VectorSeacrch.csproj.user              # VS user settings
├── VectorSeacrch.http                     # REST Client file (STALE — see issues)
├── ExtractHelper.cs                       # Extractors + chunker (UNUSED duplicate)
│
├── Controllers/
│   └── DocumentsController.cs             # 5 endpoints + private extractors/chunker
├── Data/
│   └── AppDbContext.cs                    # DbSets + cascade delete
├── Models/
│   └── Doc_Chunk.cs                       # Document, DocumentChunk, ChatRequest
├── Migrations/
│   ├── 20260926111745_intiate.cs          # Initial schema
│   ├── 20260927150616_fix_1024.cs         # Embedding column set to VECTOR(1024)
│   ├── *.Designer.cs                      # Per-migration model snapshots
│   └── AppDbContextModelSnapshot.cs       # Current model snapshot
├── Properties/
│   └── launchSettings.json                # https 44347, http 5000
├── appsettings.json                       # Connection string (PLAINTEXT PASSWORD)
├── appsettings.Development.json
└── bin/ · obj/ · .vs/
```

### Duplicated logic

Extraction and chunking exist twice:

| Location | Status |
| --- | --- |
| `Controllers/DocumentsController.cs:250-275` (private statics) | **This is what runs** |
| `ExtractHelper.cs:8-39` (public statics) | **Never called** |

The two `ChunkText` implementations are byte-identical. A fix applied to one copy will silently not affect
the running application. Consolidate into `ExtractHelper` and inject it.

---

## Prerequisites

1. **.NET SDK 9.0+** — `dotnet --version` reports 10.0.401 here
2. **SQL Server 2025** — the `VECTOR` type and `VECTOR_DISTANCE` are required. SQL Server 2022 or earlier
   will fail at query time, not at startup.
3. **Ollama**, listening on `http://localhost:11434`:
   ```powershell
   ollama pull qwen3-embedding:0.6b   # 1024-dim embeddings, required dimension
   ollama pull qwen2.5:1.5b          # answer generation
   ollama serve
   ```

---

## Setup & Run

### Connection string

`appsettings.json`:

```json
"ConnectionStrings": {
  "DefaultConnection": "Server=.\\SQL2025;Database=Dev;User Id=sa;password=<PASSWORD>;TrustServerCertificate=True;MultipleActiveResultSets=True;"
}
```

The `Dev` database is created automatically on first run. `Program.cs` throws an
`InvalidOperationException` with setup instructions if the value is missing, so misconfiguration fails
loudly at startup rather than producing a null-connection error.

> **Move this out of source control.** See [Known Issues](#known-issues) — the password is committed in
> `appsettings.json` and a commented-out copy remains at `Program.cs:17`.

### Run

```powershell
dotnet restore
dotnet run
```

Startup sequence (`Program.cs`):

1. Controllers + Swagger services registered
2. Connection string validated, `AppDbContext` bound to SQL Server
3. Shared `HttpClient` registered, configured with `BaseAddress` and no timeout
4. `IEmbeddingGenerator<string, Embedding<float>>` and `IChatClient` registered as **singletons**, both
   wrapping `OllamaApiClient` over that client
5. **Migrations applied eagerly** — `Database.Migrate()` runs in a scope before the app serves traffic
6. Swagger UI mounted in Development only, at the **root** (`RoutePrefix = ""`)

**Open <https://localhost:44347>** for Swagger UI — not `/swagger`. The `launchUrl` in `launchSettings.json`
still points at `scalar/v1`, which 404s because Scalar is not referenced.

### Verify

```powershell
curl.exe -k https://localhost:44347/api/Documents
```

### Run on a custom port (no launch profile)

```powershell
dotnet run --no-launch-profile --urls http://localhost:5199
```

---

## Configuration Reference

| Setting | Location | Current value | Notes |
| --- | --- | --- | --- |
| `ConnectionStrings:DefaultConnection` | `appsettings.json` | local `SQL2025` → `Dev` | plaintext password |
| Ollama base URI | `Program.cs:28` | `http://localhost:11434` | hardcoded |
| HTTP timeout | `Program.cs:39` | `Timeout.InfiniteTimeSpan` | |
| Embedding model | `Program.cs:44` | `qwen3-embedding:0.6b` | hardcoded; 1024 dims |
| Chat model | `Program.cs:47` | `qwen2.5:1.5b` | hardcoded |
| `EmbeddingBatchSize` | `DocumentsController.cs:22` | `32` | chunks per embedding request |
| `MaxChunksPerDocument` | `DocumentsController.cs:25` | `20_000` | → HTTP 413 |
| Chunk size | `DocumentsController.cs:125` | `250` words | inline, no overlap |
| Retrieved chunks | `DocumentsController.cs:210` | `TOP (3)` | inline |
| Distance metric | `DocumentsController.cs:212` | `cosine` | |
| Vector dimension | `Doc_Chunk.cs:38`, `DocumentsController.cs:212` | `1024` | **must stay in sync** |
| Swagger route | `Program.cs:64` | `/` (root) | Development only |

Everything above is hardcoded in source. Promote it to `appsettings.json` + `IOptions` before this runs
anywhere but localhost — particularly the vector dimension.

---

## API Reference

Base URL `https://localhost:44347` (dev, self-signed — use `curl -k`).

### `GET /api/Documents`

List all documents with chunk counts. Unpaginated.

**200**
```json
[
  { "documentID": 9, "fileName": "Kafka The Definitive Guide....pdf", "fileType": ".pdf",
    "uploadDate": "2026-09-27T15:30:32.123Z", "chunkCount": 570 }
]
```

---

### `GET /api/Documents/{id}`

One document with all chunks. **Returns full chunk text — response can be large.**

**200**
```json
{
  "documentID": 9,
  "fileName": "Kafka The Definitive Guide....pdf",
  "fileType": ".pdf",
  "uploadDate": "2026-09-27T15:30:32.123Z",
  "chunks": [ { "chunkID": 1, "chunkContent": "..." } ]
}
```

**404** — `"Document with ID {id} not found."`

---

### `POST /api/Documents/upload`

`multipart/form-data`, single field named `file`. Accepts `.txt`, `.pdf`, `.docx`.

```powershell
curl.exe -k -X POST https://localhost:44347/api/Documents/upload -F "file=@C:\docs\handbook.pdf"
```

**201**
```json
{
  "documentID": 9,
  "fileName": "Kafka The Definitive Guide....pdf",
  "totalChunks": 570,
  "message": "Document uploaded, chunked, and stored with vectors."
}
```

| Status | Condition |
| --- | --- |
| **400** | no file, zero-length file, or no extractable text |
| **413** | document yields more than 20,000 chunks |
| **500** | unsupported extension — `NotSupportedException` escapes unhandled |

The request is synchronous and CPU-bound. Progress is written to the application log:

```
Document 8 (big.txt): 2116921 bytes -> 1000 chunks
Document 8: embedded 32/1000 chunks (3%)
Document 8: embedded 64/1000 chunks (6%)
...
```

Because the work occupies a thread for its whole duration, see [Known Issues](#known-issues) for the
background-job recommendation.

---

### `DELETE /api/Documents/{id}`

Chunks are removed by the `ON DELETE CASCADE` foreign key.

**200** — `{ "message": "Document 1 and its vector chunks were deleted." }`
**404** — not found

---

### `POST /api/Documents/chat`

```json
{ "question": "What is the partition replication model?" }
```

**200**
```json
{
  "answer": "Kafka replicates partitions across brokers...",
  "retrievedSources": [ "...chunk text...", "...chunk text...", "...chunk text..." ],
  "timing": { "totalTimeMs": 1240 }
}
```

**400** — `"Question cannot be empty."`

The response is a buffered JSON string, so the answer is **not** streamed. `timing` reports total elapsed
only; the per-stage breakdown is scaffolded in comments at `DocumentsController.cs:242-244`.

---

## Data Model

### `Documents`

| Column | Type | Notes |
| --- | --- | --- |
| `DocumentID` | `int` | PK, identity |
| `FileName` | `nvarchar(255)` | required, as uploaded |
| `FileType` | `nvarchar(10)` | required, extension including dot |
| `UploadDate` | `datetime2` | `DateTime.UtcNow` |

### `DocumentChunks`

| Column | Type | Notes |
| --- | --- | --- |
| `ChunkID` | `int` | PK, identity |
| `DocumentID` | `int` | FK → `Documents`, `ON DELETE CASCADE` |
| `ChunkContent` | `nvarchar(max)` | required |
| `Embedding` | `vector` | **1024 dimensions**; JSON array string, e.g. `"[0.012,-0.44,...]"` |

The relationship and cascade are configured fluently in `AppDbContext.cs:17-21` rather than by convention,
so deleting a document always removes its vectors.

### Verifying the vector dimension

The dimension is not exposed through `INFORMATION_SCHEMA` or `sys.columns` in a readable form. Two ways to
confirm it:

```sql
-- sys.columns.max_length = 4 * dims + 8 byte header
SELECT name, max_length FROM sys.columns
WHERE object_id = OBJECT_ID('DocumentChunks') AND name = 'Embedding';
-- max_length 4104  =>  (4104 - 8) / 4 = 1024 dims

-- Functional proof: VECTOR_DISTANCE only accepts a matching dimension
SELECT TOP 1 VECTOR_DISTANCE('cosine', Embedding, CAST(@qv AS VECTOR(1024))) FROM DocumentChunks;
```

Note: `DATALENGTH(Embedding) / 4` returns **1026**, not 1024 — the extra 2 is the type's header. Do not
use it as a dimension check.

### Migrations

```powershell
dotnet ef migrations add <Name>
dotnet ef database update
```

Migrations run automatically at startup.

| Migration | Change | Status |
| --- | --- | --- |
| `20260926111745_intiate` | creates both tables | applied |
| `20260927150616_fix_1024` | `ALTER COLUMN Embedding` → `VECTOR(1024)` | applied |

A fresh database is created at the correct dimension.

---

## Verified State

Live `Dev` database at the time of writing — 9 documents, 686 chunk rows:

| ID | File | Type | Chunks |
| --- | --- | --- | --- |
| 1 | Ali_ElKomy_CV.pdf | .pdf | 0 |
| 2 | Ali_ElKomy_CV.pdf | .pdf | 8 |
| 3 | AI Automations Playbook.pdf | .pdf | 55 |
| 4 | angular_signals_guide_ar.docx | .docx | 9 |
| 5 | C#Intoduction.pdf | .pdf | 12 |
| 6 | Kafka The Definitive Guide (2nd ed.).pdf | .pdf | 0 |
| 7 | Kafka The Definitive Guide (2nd ed.).pdf | .pdf | 0 |
| 8 | big.txt | .txt | 32 *(partial)* |
| 9 | Kafka The Definitive Guide (2nd ed.).pdf | .pdf | 570 |

Observations:

- Text extraction is confirmed working for all three formats (`.pdf`, `.docx`, `.txt`).
- Documents 1, 6, 7 have **0 chunks** and document 8 has **32 of 1000** — the orphan-row defect described
  in [Known Issues](#known-issues). Four of nine rows are incomplete.
- Document 8 stopped after one batch when the client disconnected — `HttpContext.RequestAborted` fired and
  the loop exited cleanly with no unhandled exception.
- Document 9 (570 chunks) is the largest fully successful upload.

### Build status

```
Build succeeded.
5 Warning(s), 0 Error(s)
```

| Warning | Location |
| --- | --- |
| CS8981 lowercase type name `intiate` | `20260926111745_intiate.cs:9` and its `.Designer.cs:16` |
| CS8603 possible null return | `ExtractHelper.cs:17` |
| CS8602 possible null dereference | `ExtractHelper.cs:25` |
| CS8602 possible null dereference | `DocumentsController.cs:261` |

---

## Known Issues

Ordered by impact. None of these block the happy path, but several will bite on real use.

### 1. Failed uploads leave orphan rows

The `Documents` header is inserted at `DocumentsController.cs:121-122`, **before** chunking, embedding, or
the size guard. Any failure after that point leaves a permanent partial row with no way to resume it. This is
the state of 4 of 9 rows in the live database (see [Verified State](#verified-state)), currently visible only
as a `chunkCount: 0` in `GET /api/Documents`.

Three places need to move inside the same transaction as the header insert, or the header must be deleted on
failure:

- the `MaxChunksPerDocument` guard (`:127`) — rejects **after** the row exists
- an unsupported-extension `NotSupportedException` (`:107`) — thrown **before** the insert, so this one is
  actually safe
- any embedding or insert failure mid-loop

### 2. No authentication or authorization

`app.UseAuthorization()` is called at `Program.cs:70` but no authentication scheme is ever registered, so
**every endpoint is anonymous**. Combined with unlimited upload size and no rate limiting, this is
acceptable on localhost and unacceptable anywhere else. Add authentication, `[Authorize]`, and
`[RequestSizeLimit]` on the upload endpoint.

### 3. Uploads block a thread for minutes

Embedding is synchronous and CPU-bound, so a 1,000-chunk document holds a request thread for roughly two
minutes. Under concurrent uploads this exhausts the thread pool. A background queue (BackgroundService or
Channel) with the endpoint returning a job ID that the client polls is the standard shape.

### 4. RAG quality gaps

- **No provenance.** The SQL selects only `ChunkContent` (`:213`), so `retrievedSources` cannot be traced
  back to a document or filename. Select `DocumentID` too and resolve the filename.
- **No chunk overlap.** Fixed 250-word windows with no overlap mean a fact spanning a boundary is cut in
  half and becomes unretrievable. Add 10–20% overlap, or split on sentence boundaries.
- **Silent empty retrieval.** When nothing clears a relevance bar, `/chat` still answers from
  "No relevant document context found." A max-distance cutoff would let it return an honest
  "nothing relevant" instead of inviting a hallucination.
- **Whitespace-only chunking.** `ChunkText` splits on spaces and newlines, so tables, code blocks, and
  column layouts are scrambled.

### 5. Secrets in source control

The `sa` password is committed in plaintext at `appsettings.json:3`, and a commented-out duplicate remains
at `Program.cs:17`.

```powershell
dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "<YOUR_CONNECTION_STRING>"
```

Then remove the credential from `appsettings.json` (use a placeholder) and delete the commented line, and
**rotate the `sa` password** since it is in git history.

### 6. Stale and dead code

- `ChatRequest` sits in the **global namespace** (`Models/Doc_Chunk.cs:44-47`), outside
  `namespace VectorSeacrch.Models`, because the record was commented out at `:42`. It resolves at the call
  site only via the global namespace. Move it into `VectorSeacrch.Models` alongside the other types.
- `ExtractHelper.cs` — entire file unused; duplicates the controller's extractors
- `VectorSeacrch.http:3` — requests `/weatherforecast/`, an endpoint that no longer exists, on port `5147`
  (not in `launchSettings.json`)
- `launchSettings.json:7` — `launchUrl` is `scalar/v1`, but Scalar is not referenced; the app serves
  Swagger at root, so the launch URL 404s
- `csproj` comment says "Open API & Scalar UI" but only Swashbuckle is referenced
- `DocumentsController.cs:233-234`, `:242-244` — commented-out response/timing fields left mid-edit
- `ExtractHelper.cs:17` — returns `null` from a method declared non-nullable `string` (CS8603)

### 7. Robustness

- **Unsupported extension → 500.** The `switch` at `:107` throws `NotSupportedException` for an
  unrecognised extension. Validate the extension before the switch and return a `400` listing what is
  supported.
- **No dedupe.** Re-uploading a file creates a second full copy of every vector.
- **No pagination.** `GET /api/Documents` is unbounded and `GET /{id}` dumps every chunk's full text; both
  degrade as the corpus grows.
- **Scanned PDFs.** A large image-only PDF yields no text, so it returns
  `400 Could not extract readable text` rather than being detected and reported as needing OCR.

### 8. Housekeeping

- Migration classes `intiate` and `fix_1024` use lowercase type names → CS8981 ×2
- `ExtractHelper.ChunkText` and the controller copy are byte-identical; consolidate
- Spelling: project and namespace `VectorSeacrch` (intended "Search"), migration `intiate` (intended
  "initiate"), file `Doc_Chunk.cs` holding three unrelated types

---

## Troubleshooting

**`Build succeeded` but `dotnet build` reports MSB3021 / MSB3027 — "file is locked"**
A running instance is holding the output assembly. Check Task Manager or PowerShell for
`VectorSeacrch`, `dotnet`, or **IIS Express Worker Process** (the `IIS Express` launch profile in
`launchSettings.json` starts one). Stop it, or build to a different folder to bypass the lock:
```powershell
dotnet build -o "$env:TEMP\buildcheck"
```

**`VECTOR_DISTANCE` throws a dimension-mismatch error on `/chat`**
The `CAST(@p AS VECTOR(n))` in `DocumentsController.cs:212` must equal the column's declared dimension,
which must equal the embedding model's output width. Re-verify with the `sys.columns` query in
[Data Model](#data-model). Changing the embedding model means changing all three.

**`400 Could not extract readable text from the document.`**
The file parsed but yielded no text. Almost always a scanned/image-only PDF — this project does no OCR.
Confirm with `pdftotext` or by checking whether a text layer exists.

**Upload returns `201` but the document has 0 or partial chunks**
The header row is committed before embedding, so a failure mid-pipeline is visible as a short or empty
`chunkCount`. See [Known Issues #1](#known-issues). Delete the row with
`DELETE /api/Documents/{id}` and re-upload.

**Swagger UI is a 404**
It is served at the application **root** (`RoutePrefix = ""`), so <https://localhost:44347> — not
`/swagger`. The `launchUrl` of `scalar/v1` in `launchSettings.json` is stale and 404s too.

---

## Recommended Next Steps

1. Fix the orphan-row transaction issue (#1) — 44% of the live corpus is currently incomplete
2. Move embedding to a background job with progress polling (#3)
3. Rotate the `sa` password and move the connection string to user-secrets (#5)
4. Add authentication before any deployment (#2)
5. Hoist the hardcoded configuration, especially the vector dimension, into `appsettings.json`
6. Consolidate the duplicated extractors and fix the namespace/dead-code issues (#6)

---

## Testing

No test project exists. Suggested order of value:

1. **Round-trip** — upload a fixture PDF, assert chunk count, then assert `/chat` answers from that
   document. This single test would catch both dimension mismatches and upload failures.
2. **Large-upload regression** — upload a document exceeding 750 chunks and assert it completes.
3. **Dimension invariant** — assert `embedding.Vector.Length` equals the column's declared dimension before
   insert, failing with a clear message rather than a SQL dimension error. Guards against a model swap.
4. **Extractor unit tests** over fixture `.txt`/`.pdf`/`.docx`, including empty and corrupt files
5. **Chunker** — word counts, no dropped text, overlap behaviour
6. **Controller tests** for the 400 / 404 / 413 paths and unsupported extensions

---

## License

Not specified.
