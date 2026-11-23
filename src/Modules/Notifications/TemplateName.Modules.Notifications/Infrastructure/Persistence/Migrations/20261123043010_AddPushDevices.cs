using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TemplateName.Modules.Notifications.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPushDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PushDevices",
                schema: "notify",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Platform = table.Column<byte>(type: "tinyint", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    Token = table.Column<string>(type: "nvarchar(max)", maxLength: 4096, nullable: false),
                    DeviceName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AppVersion = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    LastRegisteredAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    DeactivatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    DeactivationReason = table.Column<byte>(type: "tinyint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushDevices", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PushDevices_DeactivatedAt",
                schema: "notify",
                table: "PushDevices",
                column: "DeactivatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PushDevices_TokenHash",
                schema: "notify",
                table: "PushDevices",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushDevices_UserId",
                schema: "notify",
                table: "PushDevices",
                column: "UserId",
                filter: "[DeactivatedAt] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PushDevices",
                schema: "notify");
        }
    }
}
