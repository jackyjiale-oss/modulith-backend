using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TemplateName.Modules.Auth.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPhoneNumbers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PhoneNumber",
                schema: "auth",
                table: "Users",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PhoneNumberVerifiedAt",
                schema: "auth",
                table: "Users",
                type: "datetime2(3)",
                precision: 3,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PhoneVerificationCodes",
                schema: "auth",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PhoneNumber = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CodeHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    Salt = table.Column<byte[]>(type: "varbinary(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    ConsumedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    InvalidatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhoneVerificationCodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhoneVerificationCodes_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "auth",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_PhoneNumber",
                schema: "auth",
                table: "Users",
                column: "PhoneNumber",
                unique: true,
                filter: "[PhoneNumber] IS NOT NULL AND [IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_PhoneVerificationCodes_ExpiresAt",
                schema: "auth",
                table: "PhoneVerificationCodes",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_PhoneVerificationCodes_UserId_CreatedAt",
                schema: "auth",
                table: "PhoneVerificationCodes",
                columns: new[] { "UserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PhoneVerificationCodes",
                schema: "auth");

            migrationBuilder.DropIndex(
                name: "IX_Users_PhoneNumber",
                schema: "auth",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PhoneNumber",
                schema: "auth",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PhoneNumberVerifiedAt",
                schema: "auth",
                table: "Users");
        }
    }
}
