using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TemplateName.Modules.Auth.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionListIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserSessions_UserId",
                schema: "auth",
                table: "UserSessions");

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_UserId_CreatedAt_Id",
                schema: "auth",
                table: "UserSessions",
                columns: new[] { "UserId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_UserId_LastSeenAt_Id",
                schema: "auth",
                table: "UserSessions",
                columns: new[] { "UserId", "LastSeenAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserSessions_UserId_CreatedAt_Id",
                schema: "auth",
                table: "UserSessions");

            migrationBuilder.DropIndex(
                name: "IX_UserSessions_UserId_LastSeenAt_Id",
                schema: "auth",
                table: "UserSessions");

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_UserId",
                schema: "auth",
                table: "UserSessions",
                column: "UserId");
        }
    }
}
