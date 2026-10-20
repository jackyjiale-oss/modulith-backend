using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TemplateName.Modules.Sample.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCleanupIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_ProcessedAt",
                schema: "sample",
                table: "OutboxMessages",
                column: "ProcessedAt",
                filter: "[ProcessedAt] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_ProcessedAt",
                schema: "sample",
                table: "OutboxMessages");
        }
    }
}
