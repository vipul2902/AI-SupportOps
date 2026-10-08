using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace AISupportOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChunkEmbeddings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Vector>(
                name: "embedding",
                table: "document_chunks",
                type: "vector(1536)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "embedding_model",
                table: "document_chunks",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_document_chunks_embedding",
                table: "document_chunks",
                column: "embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_document_chunks_embedding",
                table: "document_chunks");

            migrationBuilder.DropColumn(
                name: "embedding",
                table: "document_chunks");

            migrationBuilder.DropColumn(
                name: "embedding_model",
                table: "document_chunks");
        }
    }
}
