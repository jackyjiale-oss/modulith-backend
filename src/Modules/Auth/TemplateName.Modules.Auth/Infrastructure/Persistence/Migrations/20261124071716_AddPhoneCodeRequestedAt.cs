using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TemplateName.Modules.Auth.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPhoneCodeRequestedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PhoneCodeRequestedAt",
                schema: "auth",
                table: "Users",
                type: "datetime2(3)",
                precision: 3,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PhoneCodeRequestedAt",
                schema: "auth",
                table: "Users");
        }
    }
}
