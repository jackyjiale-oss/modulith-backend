using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TemplateName.Modules.Notifications.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryAdminIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Notifications_RecipientUserId",
                schema: "notify",
                table: "Notifications",
                column: "RecipientUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_Status_CreatedAt_Id",
                schema: "notify",
                table: "Deliveries",
                columns: new[] { "Status", "CreatedAt", "Id" },
                descending: new[] { false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Notifications_RecipientUserId",
                schema: "notify",
                table: "Notifications");

            migrationBuilder.DropIndex(
                name: "IX_Deliveries_Status_CreatedAt_Id",
                schema: "notify",
                table: "Deliveries");
        }
    }
}
