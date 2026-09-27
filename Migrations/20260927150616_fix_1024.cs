using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VectorSeacrch.Migrations
{
    /// <inheritdoc />
    public partial class fix_1024 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Embedding",
                table: "DocumentChunks",
                type: "VECTOR(1024)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "VECTOR(768)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Embedding",
                table: "DocumentChunks",
                type: "VECTOR(768)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "VECTOR(1024)");
        }
    }
}
