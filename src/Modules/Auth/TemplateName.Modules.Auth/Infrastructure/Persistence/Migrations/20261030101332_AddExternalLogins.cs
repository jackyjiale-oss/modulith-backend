using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TemplateName.Modules.Auth.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalLogins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FirstFactor",
                schema: "auth",
                table: "MfaChallenges",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "pwd");

            migrationBuilder.CreateTable(
                name: "UserExternalLogins",
                schema: "auth",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    LinkedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserExternalLogins", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserExternalLogins_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "auth",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserExternalLogins_Provider_Subject",
                schema: "auth",
                table: "UserExternalLogins",
                columns: new[] { "Provider", "Subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserExternalLogins_UserId_Provider",
                schema: "auth",
                table: "UserExternalLogins",
                columns: new[] { "UserId", "Provider" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserExternalLogins",
                schema: "auth");

            migrationBuilder.DropColumn(
                name: "FirstFactor",
                schema: "auth",
                table: "MfaChallenges");
        }
    }
}
