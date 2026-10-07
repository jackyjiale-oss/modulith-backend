using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TemplateName.Modules.Sample.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLeaveRequestListIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LeaveRequests_EmployeeId",
                schema: "sample",
                table: "LeaveRequests");

            migrationBuilder.CreateIndex(
                name: "IX_LeaveRequests_CreatedAt_Id",
                schema: "sample",
                table: "LeaveRequests",
                columns: new[] { "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaveRequests_EmployeeId_CreatedAt_Id",
                schema: "sample",
                table: "LeaveRequests",
                columns: new[] { "EmployeeId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaveRequests_StartDate_Id",
                schema: "sample",
                table: "LeaveRequests",
                columns: new[] { "StartDate", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LeaveRequests_CreatedAt_Id",
                schema: "sample",
                table: "LeaveRequests");

            migrationBuilder.DropIndex(
                name: "IX_LeaveRequests_EmployeeId_CreatedAt_Id",
                schema: "sample",
                table: "LeaveRequests");

            migrationBuilder.DropIndex(
                name: "IX_LeaveRequests_StartDate_Id",
                schema: "sample",
                table: "LeaveRequests");

            migrationBuilder.CreateIndex(
                name: "IX_LeaveRequests_EmployeeId",
                schema: "sample",
                table: "LeaveRequests",
                column: "EmployeeId");
        }
    }
}
