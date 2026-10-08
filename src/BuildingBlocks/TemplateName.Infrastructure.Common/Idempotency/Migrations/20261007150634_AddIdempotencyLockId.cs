using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TemplateName.Infrastructure.Common.Idempotency.Migrations
{
    /// <inheritdoc />
    public partial class AddIdempotencyLockId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LockId",
                schema: "platform",
                table: "IdempotencyKeys",
                type: "uniqueidentifier",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LockId",
                schema: "platform",
                table: "IdempotencyKeys");
        }
    }
}
