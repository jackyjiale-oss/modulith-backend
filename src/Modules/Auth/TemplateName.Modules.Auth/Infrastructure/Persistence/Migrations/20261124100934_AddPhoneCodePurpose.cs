using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TemplateName.Modules.Auth.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPhoneCodePurpose : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "Purpose",
                schema: "auth",
                table: "PhoneVerificationCodes",
                type: "tinyint",
                nullable: false,
                // The rows already there are verification codes (PhoneCodePurpose.Verify). The application always writes the purpose.
                defaultValue: (byte)1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Purpose",
                schema: "auth",
                table: "PhoneVerificationCodes");
        }
    }
}
