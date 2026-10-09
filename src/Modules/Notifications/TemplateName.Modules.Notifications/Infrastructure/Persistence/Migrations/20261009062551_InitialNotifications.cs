using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TemplateName.Modules.Notifications.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "notify");

            migrationBuilder.CreateTable(
                name: "HubTickets",
                schema: "notify",
                columns: table => new
                {
                    TokenHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    ConsumedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HubTickets", x => x.TokenHash);
                });

            migrationBuilder.CreateTable(
                name: "InAppNotifications",
                schema: "notify",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NotificationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TypeCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Category = table.Column<byte>(type: "tinyint", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    ReadAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InAppNotifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InboxMessages",
                schema: "notify",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Consumer = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxMessages", x => new { x.MessageId, x.Consumer });
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                schema: "notify",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TypeCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RecipientUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Priority = table.Column<byte>(type: "tinyint", nullable: false),
                    Culture = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Data = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ProtectedData = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    SourceMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserPreferences",
                schema: "notify",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TypeCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Channel = table.Column<byte>(type: "tinyint", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPreferences", x => new { x.UserId, x.TypeCode, x.Channel });
                });

            migrationBuilder.CreateTable(
                name: "UserSettings",
                schema: "notify",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    QuietHoursEnd = table.Column<TimeOnly>(type: "time", nullable: true),
                    QuietHoursStart = table.Column<TimeOnly>(type: "time", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSettings", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "Deliveries",
                schema: "notify",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NotificationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<byte>(type: "tinyint", nullable: false),
                    Destination = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    LockedUntil = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RenderedSubject = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    SentAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    DeadLetteredAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Deliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Deliveries_Notifications_NotificationId",
                        column: x => x.NotificationId,
                        principalSchema: "notify",
                        principalTable: "Notifications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_CreatedAt_Id",
                schema: "notify",
                table: "Deliveries",
                columns: new[] { "CreatedAt", "Id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_NotificationId",
                schema: "notify",
                table: "Deliveries",
                column: "NotificationId");

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_Status_NextAttemptAt",
                schema: "notify",
                table: "Deliveries",
                columns: new[] { "Status", "NextAttemptAt" },
                filter: "[Status] = 0")
                .Annotation("SqlServer:Include", new[] { "Channel", "LockedUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_InAppNotifications_UserId",
                schema: "notify",
                table: "InAppNotifications",
                column: "UserId",
                filter: "[ReadAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_InAppNotifications_UserId_CreatedAt_Id",
                schema: "notify",
                table: "InAppNotifications",
                columns: new[] { "UserId", "CreatedAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_SourceMessageId_TypeCode_RecipientUserId",
                schema: "notify",
                table: "Notifications",
                columns: new[] { "SourceMessageId", "TypeCode", "RecipientUserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Deliveries",
                schema: "notify");

            migrationBuilder.DropTable(
                name: "HubTickets",
                schema: "notify");

            migrationBuilder.DropTable(
                name: "InAppNotifications",
                schema: "notify");

            migrationBuilder.DropTable(
                name: "InboxMessages",
                schema: "notify");

            migrationBuilder.DropTable(
                name: "UserPreferences",
                schema: "notify");

            migrationBuilder.DropTable(
                name: "UserSettings",
                schema: "notify");

            migrationBuilder.DropTable(
                name: "Notifications",
                schema: "notify");
        }
    }
}
