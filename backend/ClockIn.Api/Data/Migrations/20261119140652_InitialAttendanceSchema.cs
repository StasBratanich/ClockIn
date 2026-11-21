using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace ClockIn.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialAttendanceSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Employees",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmployeeNumber = table.Column<string>(type: "varchar(20)", nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(120)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Employees", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FailedPunchAttempts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmployeeId = table.Column<int>(type: "int", nullable: false),
                    Action = table.Column<byte>(type: "tinyint", nullable: false),
                    FailureCode = table.Column<string>(type: "varchar(32)", nullable: false),
                    ReceivedAtServerUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FailedPunchAttempts", x => x.Id);
                    table.CheckConstraint("CK_FailedPunchAttempts_Action", "[Action] IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_FailedPunchAttempts_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Shifts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmployeeId = table.Column<int>(type: "int", nullable: false),
                    ClockInAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ClockOutAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shifts", x => x.Id);
                    table.CheckConstraint("CK_Shifts_StatusAndClockOut", "([Status] IN (1, 3) AND [ClockOutAtUtc] IS NULL) OR ([Status] = 2 AND [ClockOutAtUtc] IS NOT NULL AND [ClockOutAtUtc] > [ClockInAtUtc])");
                    table.CheckConstraint("CK_Shifts_UtcOffsets", "DATEPART(TZOFFSET, [ClockInAtUtc]) = 0 AND ([ClockOutAtUtc] IS NULL OR DATEPART(TZOFFSET, [ClockOutAtUtc]) = 0)");
                    table.ForeignKey(
                        name: "FK_Shifts_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id");
                });

            migrationBuilder.InsertData(
                table: "Employees",
                columns: new[] { "Id", "EmployeeNumber", "FullName" },
                values: new object[,]
                {
                    { 1, "1001", "Demo Employee 1001" },
                    { 2, "1002", "Demo Employee 1002" },
                    { 3, "1003", "Demo Employee 1003" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Employees_EmployeeNumber",
                table: "Employees",
                column: "EmployeeNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FailedPunchAttempts_EmployeeId",
                table: "FailedPunchAttempts",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_Shifts_EmployeeId_Open",
                table: "Shifts",
                column: "EmployeeId",
                unique: true,
                filter: "[Status] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FailedPunchAttempts");

            migrationBuilder.DropTable(
                name: "Shifts");

            migrationBuilder.DropTable(
                name: "Employees");
        }
    }
}
