using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using UglyToad.PdfPig;
using VectorSeacrch.Data;
using VectorSeacrch.Models;
using System.Diagnostics;

namespace VectorSeacrch.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class DocumentsController : ControllerBase
    {
        /// <summary>
        /// Chunks per embedding request. Ollama embeds a batch serially at roughly
        /// 130ms/chunk on CPU, so 32 keeps each HTTP round-trip around 4s — comfortably
        /// inside any sane timeout — while still amortising the request overhead.
        /// </summary>
        private const int EmbeddingBatchSize = 32;

        /// <summary>Hard ceiling on chunks per document, to reject absurd uploads up front.</summary>
        private const int MaxChunksPerDocument = 20_000;

        private readonly AppDbContext _context;
        private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
        private readonly IChatClient _chatClient;
        private readonly ILogger<DocumentsController> _logger;

        public DocumentsController(
            AppDbContext context,
            IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
            IChatClient chatClient,
            ILogger<DocumentsController> logger)
        {
            _context = context;
            _embeddingGenerator = embeddingGenerator;
            _chatClient = chatClient;
            _logger = logger;
        }

        // =========================================================================
        // CRUD: READ ALL
        // =========================================================================
        [HttpGet]
        public async Task<IActionResult> GetAllDocuments()
        {
            var docs = await _context.Documents
                .Select(d => new
                {
                    d.DocumentID,
                    d.FileName,
                    d.FileType,
                    d.UploadDate,
                    ChunkCount = d.Chunks.Count
                })
                .ToListAsync();

            return Ok(docs);
        }

        // =========================================================================
        // CRUD: READ BY ID
        // =========================================================================
        [HttpGet("{id:int}")]
        public async Task<IActionResult> GetDocumentById(int id)
        {
            var doc = await _context.Documents
                .Include(d => d.Chunks)
                .Where(d => d.DocumentID == id)
                .Select(d => new
                {
                    d.DocumentID,
                    d.FileName,
                    d.FileType,
                    d.UploadDate,
                    Chunks = d.Chunks.Select(c => new { c.ChunkID, c.ChunkContent })
                })
                .FirstOrDefaultAsync();

            if (doc == null)
                return NotFound($"Document with ID {id} not found.");

            return Ok(doc);
        }

        // =========================================================================
        // CRUD: CREATE (Upload, Extract, Chunk & Vectorize)
        // =========================================================================
        [HttpPost("upload")]
        public async Task<IActionResult> UploadDocument(IFormFile file)
        {
            if (file == null || file.Length == 0)
                return BadRequest("No file was provided.");

            string extension = Path.GetExtension(file.FileName).ToLowerInvariant();

            if (extension is not (".txt" or ".pdf" or ".docx"))
                return BadRequest($"Unsupported file format '{extension}'. Supported: .txt, .pdf, .docx.");

            using var stream = file.OpenReadStream();

            // 1. Extract Text
            string documentText = extension switch
            {
                ".txt" => await new StreamReader(stream).ReadToEndAsync(),
                ".pdf" => ExtractPdfText(stream),
                ".docx" => ExtractDocxText(stream),
                _ => throw new NotSupportedException($"Unsupported file format: {extension}")
            };

            if (string.IsNullOrWhiteSpace(documentText))
                return BadRequest("Could not extract readable text from the document.");

            // 2. Create Document Entity
            var document = new Document
            {
                FileName = file.FileName,
                FileType = extension,
                UploadDate = DateTime.UtcNow
            };

            _context.Documents.Add(document);
            await _context.SaveChangesAsync(); // Generates DocumentID

            // 3. Chunk
            var textChunks = ChunkText(documentText, wordsPerChunk: 250);

            if (textChunks.Count > MaxChunksPerDocument)
                return StatusCode(StatusCodes.Status413PayloadTooLarge,
                    $"Document yields {textChunks.Count} chunks, above the {MaxChunksPerDocument} limit.");

            _logger.LogInformation(
                "Document {DocumentId} ({FileName}): {FileSizeBytes} bytes -> {ChunkCount} chunks",
                document.DocumentID, document.FileName, file.Length, textChunks.Count);

            // 4. Embed and persist in bounded batches.
            // Sending every chunk in one request is what broke large uploads: the payload
            // runs to megabytes, the whole run occupies a single connection, and the
            // client aborts it once the timeout elapses. Batching keeps each request
            // short, writes incrementally so a failure does not lose everything, and
            // reports progress on what is a multi-minute job.
            for (int offset = 0; offset < textChunks.Count; offset += EmbeddingBatchSize)
            {
                var batch = textChunks.Skip(offset).Take(EmbeddingBatchSize).ToArray();

                var batchEmbeddings = await _embeddingGenerator.GenerateAsync(
                    batch,
                    cancellationToken: HttpContext.RequestAborted);

                foreach (var (chunkText, embedding) in batch.Zip(batchEmbeddings))
                {
                    _context.DocumentChunks.Add(new DocumentChunk
                    {
                        DocumentID = document.DocumentID,
                        ChunkContent = chunkText,
                        Embedding = JsonSerializer.Serialize(embedding.Vector.ToArray())
                    });
                }

                await _context.SaveChangesAsync();

                int done = offset + batch.Length;
                _logger.LogInformation(
                    "Document {DocumentId}: embedded {Done}/{Total} chunks ({Percent:F0}%)",
                    document.DocumentID, done, textChunks.Count, 100.0 * done / textChunks.Count);
            }

            return CreatedAtAction(nameof(GetDocumentById), new { id = document.DocumentID }, new
            {
                document.DocumentID,
                document.FileName,
                TotalChunks = textChunks.Count,
                Message = "Document uploaded, chunked, and stored with vectors."
            });
        }

        // =========================================================================
        // CRUD: DELETE
        // =========================================================================
        [HttpDelete("{id:int}")]
        public async Task<IActionResult> DeleteDocument(int id)
        {
            var doc = await _context.Documents.FindAsync(id);
            if (doc == null)
                return NotFound($"Document with ID {id} not found.");

            _context.Documents.Remove(doc);
            await _context.SaveChangesAsync();

            return Ok(new { Message = $"Document {id} and its vector chunks were deleted." });
        }

        // =========================================================================
        // RAG: SEARCH & CHAT WITH LLAMA
        // =========================================================================
        [HttpPost("chat")]
        public async Task<IActionResult> ChatWithDocuments([FromBody] ChatRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Question))
                return BadRequest("Question cannot be empty.");
            // Start timing
            var stopwatch = Stopwatch.StartNew();

            // 1. Convert Query to Vector via Ollama
            var queryEmbedding = await _embeddingGenerator.GenerateAsync(new[] { request.Question });
            string jsonQueryVector = JsonSerializer.Serialize(queryEmbedding[0].Vector.ToArray());

            // 2. Perform Native SQL Server 2025 Vector Search via EF Core FromSqlInterpolated
            var contextChunks = await _context.DocumentChunks
                .FromSqlInterpolated($@"
                    SELECT TOP (3) ChunkID, DocumentID, ChunkContent, Embedding 
                    FROM DocumentChunks 
                    ORDER BY VECTOR_DISTANCE('cosine', Embedding, CAST({jsonQueryVector} AS VECTOR(1024))) ASC")
                .Select(c => c.ChunkContent)
                .ToListAsync();

            // 3. Build Prompt and Call Llama 3.2
            string contextText = contextChunks.Count > 0
                ? string.Join("\n---\n", contextChunks)
                : "No relevant document context found.";

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, "Answer the question using ONLY the provided document context. If the context does not contain enough information, state that clearly."),
                new(ChatRole.User, $"Context:\n{contextText}\n\nQuestion: {request.Question}")
            };

            var response = await _chatClient.GetResponseAsync(messages);
            
            stopwatch.Stop();
            
            return Ok(new
            {
                //Answer = response.Text,
                //RetrievedSources = contextChunks

                Answer = response.Text,
                RetrievedSources = contextChunks,
                Timing = new
                {
                    TotalTimeMs = stopwatch.ElapsedMilliseconds,
                    //EmbeddingMs = embedElapsedMs,
                    //DatabaseQueryMs = dbElapsedMs,
                    //AiGenerationMs = aiElapsedMs
                }
            });
        }

        // =========================================================================
        // RAG: STREAMING CHAT (SSE)
        // =========================================================================
        // Emits named SSE events the Angular ChatStreamService can distinguish:
        //   sources → retrieved chunks, token → answer delta, done → timing, error → message
        [HttpPost("chat/stream")]
        public async Task ChatStream([FromBody] ChatRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Question))
            {
                Response.StatusCode = StatusCodes.Status400BadRequest;
                await Response.WriteAsync("Question cannot be empty.");
                return;
            }

            Response.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-cache";

            var stopwatch = Stopwatch.StartNew();

            // 1. Convert Query to Vector via Ollama
            var queryEmbedding = await _embeddingGenerator.GenerateAsync(new[] { request.Question });
            string jsonQueryVector = JsonSerializer.Serialize(queryEmbedding[0].Vector.ToArray());

            // 2. Perform Native SQL Server 2025 Vector Search
            var contextChunks = await _context.DocumentChunks
                .FromSqlInterpolated($@"
                    SELECT TOP (3) ChunkID, DocumentID, ChunkContent, Embedding
                    FROM DocumentChunks
                    ORDER BY VECTOR_DISTANCE('cosine', Embedding, CAST({jsonQueryVector} AS VECTOR(1024))) ASC")
                .Select(c => c.ChunkContent)
                .ToListAsync();

            // 3. Build Prompt
            string contextText = contextChunks.Count > 0
                ? string.Join("\n---\n", contextChunks)
                : "No relevant document context found.";

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, "Answer the question using ONLY the provided document context. If the context does not contain enough information, state that clearly."),
                new(ChatRole.User, $"Context:\n{contextText}\n\nQuestion: {request.Question}")
            };

            await using var writer = new StreamWriter(Response.Body);

            // 4. Send the retrieved context first so the UI can show sources immediately.
            await WriteEventAsync(writer, "sources", contextChunks);

            // 5. Stream the answer token by token.
            try
            {
                await foreach (var update in _chatClient.GetStreamingResponseAsync(
                    messages, cancellationToken: HttpContext.RequestAborted))
                {
                    if (!string.IsNullOrEmpty(update.Text))
                    {
                        await WriteEventAsync(writer, "token", update.Text);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Client disconnected (e.g. Stop button) — nothing more to send.
                return;
            }
            catch (Exception ex)
            {
                await WriteEventAsync(writer, "error", new { message = ex.Message });
                return;
            }

            stopwatch.Stop();
            await WriteEventAsync(writer, "done", new { totalTimeMs = stopwatch.ElapsedMilliseconds });
        }

        private static async Task WriteEventAsync(StreamWriter writer, string name, object data)
        {
            await writer.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(data)}\n\n");
            await writer.FlushAsync();
        }

        #region Document Extractors & Chunking Helpers

        private static string ExtractPdfText(Stream stream)
        {
            stream.Position = 0;
            using var pdf = PdfDocument.Open(stream);
            return string.Join("\n", pdf.GetPages().Select(p => p.Text));
        }

        private static string ExtractDocxText(Stream stream)
        {
            stream.Position = 0;
            using var doc = WordprocessingDocument.Open(stream, false);
            return doc.MainDocumentPart?.Document.Body?.InnerText ?? string.Empty;
        }

        private static List<string> ChunkText(string text, int wordsPerChunk)
        {
            string[] words = text.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var chunks = new List<string>();

            for (int i = 0; i < words.Length; i += wordsPerChunk)
            {
                chunks.Add(string.Join(" ", words.Skip(i).Take(wordsPerChunk)));
            }

            return chunks;
        }

        #endregion
    }
}
