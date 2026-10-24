using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TemplateName.Infrastructure.Common.Idempotency.Migrations
{
    /// <inheritdoc />
    public partial class AddJobStatesAndExpiryIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JobStates",
                schema: "platform",
                columns: table => new
                {
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NextRunAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    LockedUntil = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    LastStartedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    LastFinishedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    LastStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    LastProcessed = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobStates", x => x.Name);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyKeys_ExpiresAt",
                schema: "platform",
                table: "IdempotencyKeys",
                column: "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobStates",
                schema: "platform");

            migrationBuilder.DropIndex(
                name: "IX_IdempotencyKeys_ExpiresAt",
                schema: "platform",
                table: "IdempotencyKeys");
        }
    }
}
