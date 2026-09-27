using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VectorSeacrch.Models
{
    public class Document
    {
        [Key]
        public int DocumentID { get; set; }

        [Required]
        [MaxLength(255)]
        public string FileName { get; set; } = string.Empty;

        [Required]
        [MaxLength(10)]
        public string FileType { get; set; } = string.Empty;

        public DateTime UploadDate { get; set; } = DateTime.UtcNow;

        public ICollection<DocumentChunk> Chunks { get; set; } = new List<DocumentChunk>();
    }

    public class DocumentChunk
    {
        [Key]
        public int ChunkID { get; set; }

        public int DocumentID { get; set; }

        [ForeignKey(nameof(DocumentID))]
        public Document? Document { get; set; }

        [Required]
        public string ChunkContent { get; set; } = string.Empty;

        // Mapped to SQL Server 2025 VECTOR(1024)
        [Column(TypeName = "VECTOR(1024)")]
        public string Embedding { get; set; } = string.Empty;
    }

    //public record ChatRequest(string Question);
}
public class ChatRequest
{
    public string Question { get; set; } = string.Empty;
}
