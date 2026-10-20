using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TemplateName.Modules.Auth.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCleanupIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_VerificationCodes_ExpiresAt",
                schema: "auth",
                table: "VerificationCodes",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_ExpiresAt",
                schema: "auth",
                table: "UserSessions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_RevokedAt",
                schema: "auth",
                table: "UserSessions",
                column: "RevokedAt",
                filter: "[RevokedAt] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TrustedDevices_ExpiresAt",
                schema: "auth",
                table: "TrustedDevices",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_TrustedDevices_RevokedAt",
                schema: "auth",
                table: "TrustedDevices",
                column: "RevokedAt",
                filter: "[RevokedAt] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_ExpiresAt",
                schema: "auth",
                table: "RefreshTokens",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_ProcessedAt",
                schema: "auth",
                table: "OutboxMessages",
                column: "ProcessedAt",
                filter: "[ProcessedAt] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MfaChallenges_ExpiresAt",
                schema: "auth",
                table: "MfaChallenges",
                column: "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VerificationCodes_ExpiresAt",
                schema: "auth",
                table: "VerificationCodes");

            migrationBuilder.DropIndex(
                name: "IX_UserSessions_ExpiresAt",
                schema: "auth",
                table: "UserSessions");

            migrationBuilder.DropIndex(
                name: "IX_UserSessions_RevokedAt",
                schema: "auth",
                table: "UserSessions");

            migrationBuilder.DropIndex(
                name: "IX_TrustedDevices_ExpiresAt",
                schema: "auth",
                table: "TrustedDevices");

            migrationBuilder.DropIndex(
                name: "IX_TrustedDevices_RevokedAt",
                schema: "auth",
                table: "TrustedDevices");

            migrationBuilder.DropIndex(
                name: "IX_RefreshTokens_ExpiresAt",
                schema: "auth",
                table: "RefreshTokens");

            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_ProcessedAt",
                schema: "auth",
                table: "OutboxMessages");

            migrationBuilder.DropIndex(
                name: "IX_MfaChallenges_ExpiresAt",
                schema: "auth",
                table: "MfaChallenges");
        }
    }
}
