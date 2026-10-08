using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TemplateName.Modules.Auth.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRoleAndAuditLogListIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuthAuditLogs_EventType_OccurredAt",
                schema: "auth",
                table: "AuthAuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_AuthAuditLogs_UserId_OccurredAt",
                schema: "auth",
                table: "AuthAuditLogs");

            migrationBuilder.CreateIndex(
                name: "IX_Roles_CreatedAt_Id",
                schema: "auth",
                table: "Roles",
                columns: new[] { "CreatedAt", "Id" },
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_AuthAuditLogs_EventType_OccurredAt_Id",
                schema: "auth",
                table: "AuthAuditLogs",
                columns: new[] { "EventType", "OccurredAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AuthAuditLogs_OccurredAt_Id",
                schema: "auth",
                table: "AuthAuditLogs",
                columns: new[] { "OccurredAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AuthAuditLogs_UserId_OccurredAt_Id",
                schema: "auth",
                table: "AuthAuditLogs",
                columns: new[] { "UserId", "OccurredAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Roles_CreatedAt_Id",
                schema: "auth",
                table: "Roles");

            migrationBuilder.DropIndex(
                name: "IX_AuthAuditLogs_EventType_OccurredAt_Id",
                schema: "auth",
                table: "AuthAuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_AuthAuditLogs_OccurredAt_Id",
                schema: "auth",
                table: "AuthAuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_AuthAuditLogs_UserId_OccurredAt_Id",
                schema: "auth",
                table: "AuthAuditLogs");

            migrationBuilder.CreateIndex(
                name: "IX_AuthAuditLogs_EventType_OccurredAt",
                schema: "auth",
                table: "AuthAuditLogs",
                columns: new[] { "EventType", "OccurredAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_AuthAuditLogs_UserId_OccurredAt",
                schema: "auth",
                table: "AuthAuditLogs",
                columns: new[] { "UserId", "OccurredAt" },
                descending: new[] { false, true });
        }
    }
}
