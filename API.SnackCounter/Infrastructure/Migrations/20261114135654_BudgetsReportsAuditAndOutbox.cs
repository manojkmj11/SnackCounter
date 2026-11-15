using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace API.SnackCounter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BudgetsReportsAuditAndOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ActorId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    EntityId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ChangeSummaryJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MonthlyBudgets",
                columns: table => new
                {
                    Office = table.Column<int>(type: "int", nullable: false),
                    Year = table.Column<int>(type: "int", nullable: false),
                    Month = table.Column<int>(type: "int", nullable: false),
                    Currency = table.Column<int>(type: "int", nullable: false),
                    BudgetAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    SpentAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    WarningThresholdPercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    SnapshotAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonthlyBudgets", x => new { x.Office, x.Year, x.Month });
                    table.CheckConstraint("CK_MonthlyBudgets_YearMonth", "[Year] >= 2000 AND [Month] BETWEEN 1 AND 12 AND [BudgetAmount] >= 0 AND [SpentAmount] >= 0");
                    table.ForeignKey(
                        name: "FK_MonthlyBudgets_Offices_Office",
                        column: x => x.Office,
                        principalTable: "Offices",
                        principalColumn: "Location",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MonthlyReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Office = table.Column<int>(type: "int", nullable: true),
                    Year = table.Column<int>(type: "int", nullable: false),
                    Month = table.Column<int>(type: "int", nullable: false),
                    AsOf = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReportingCurrency = table.Column<int>(type: "int", nullable: true),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    GeneratedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonthlyReports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MonthlyReports_Offices_Office",
                        column: x => x.Office,
                        principalTable: "Offices",
                        principalColumn: "Location",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AvailableAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LockedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditRecords_ActorId_OccurredAt",
                table: "AuditRecords",
                columns: new[] { "ActorId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditRecords_EntityType_EntityId_OccurredAt",
                table: "AuditRecords",
                columns: new[] { "EntityType", "EntityId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MonthlyReports_Office",
                table: "MonthlyReports",
                column: "Office");

            migrationBuilder.CreateIndex(
                name: "IX_MonthlyReports_Year_Month",
                table: "MonthlyReports",
                columns: new[] { "Year", "Month" },
                unique: true,
                filter: "[Office] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MonthlyReports_Year_Month_Office",
                table: "MonthlyReports",
                columns: new[] { "Year", "Month", "Office" },
                unique: true,
                filter: "[Office] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_EventId",
                table: "OutboxMessages",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_ProcessedAt_AvailableAt_LockedUntil",
                table: "OutboxMessages",
                columns: new[] { "ProcessedAt", "AvailableAt", "LockedUntil" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditRecords");

            migrationBuilder.DropTable(
                name: "MonthlyBudgets");

            migrationBuilder.DropTable(
                name: "MonthlyReports");

            migrationBuilder.DropTable(
                name: "OutboxMessages");
        }
    }
}
