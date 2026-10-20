using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TemplateName.Modules.Notifications.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCleanupIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Notifications_CreatedAt",
                schema: "notify",
                table: "Notifications",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_InboxMessages_ProcessedAt",
                schema: "notify",
                table: "InboxMessages",
                column: "ProcessedAt");

            migrationBuilder.CreateIndex(
                name: "IX_InAppNotifications_CreatedAt",
                schema: "notify",
                table: "InAppNotifications",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_HubTickets_ExpiresAt",
                schema: "notify",
                table: "HubTickets",
                column: "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Notifications_CreatedAt",
                schema: "notify",
                table: "Notifications");

            migrationBuilder.DropIndex(
                name: "IX_InboxMessages_ProcessedAt",
                schema: "notify",
                table: "InboxMessages");

            migrationBuilder.DropIndex(
                name: "IX_InAppNotifications_CreatedAt",
                schema: "notify",
                table: "InAppNotifications");

            migrationBuilder.DropIndex(
                name: "IX_HubTickets_ExpiresAt",
                schema: "notify",
                table: "HubTickets");
        }
    }
}
