using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace API.SnackCounter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReimbursementClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReimbursementClaims",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Office = table.Column<int>(type: "int", nullable: false),
                    RefillId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Currency = table.Column<int>(type: "int", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ReceiptStorageKey = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ReceiptMediaType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ReceiptSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    ReceiptSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ProcessingByEmployeeId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    ProcessingAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ProcessingLeaseExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DecisionReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReimbursedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PayrollAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    PayrollCurrency = table.Column<int>(type: "int", nullable: true),
                    ExchangeRate = table.Column<decimal>(type: "decimal(19,8)", precision: 19, scale: 8, nullable: true),
                    ExchangeRateAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ExchangeRateSource = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReimbursementClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReimbursementClaims_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReimbursementClaims_Employees_ProcessingByEmployeeId",
                        column: x => x.ProcessingByEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReimbursementClaims_Offices_Office",
                        column: x => x.Office,
                        principalTable: "Offices",
                        principalColumn: "Location",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReimbursementClaims_Refills_RefillId",
                        column: x => x.RefillId,
                        principalTable: "Refills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReimbursementClaims_EmployeeId",
                table: "ReimbursementClaims",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_ReimbursementClaims_IdempotencyKey",
                table: "ReimbursementClaims",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReimbursementClaims_Office",
                table: "ReimbursementClaims",
                column: "Office");

            migrationBuilder.CreateIndex(
                name: "IX_ReimbursementClaims_ProcessingByEmployeeId",
                table: "ReimbursementClaims",
                column: "ProcessingByEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_ReimbursementClaims_RefillId",
                table: "ReimbursementClaims",
                column: "RefillId");

            migrationBuilder.CreateIndex(
                name: "IX_ReimbursementClaims_Status_SubmittedAt",
                table: "ReimbursementClaims",
                columns: new[] { "Status", "SubmittedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReimbursementClaims");
        }
    }
}
