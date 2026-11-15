using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MockHr.Service.Migrations
{
    /// <inheritdoc />
    public partial class InitialMockHr : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Claims",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SnackCounterClaimId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    EmployeeId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Office = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ReceiptBase64 = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ReceiptMediaType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    DecisionReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Claims", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebhookOutbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalClaimId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AvailableAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LockedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookOutbox", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebhookOutbox_Claims_ExternalClaimId",
                        column: x => x.ExternalClaimId,
                        principalTable: "Claims",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Claims_IdempotencyKey",
                table: "Claims",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Claims_Status_SubmittedAt",
                table: "Claims",
                columns: new[] { "Status", "SubmittedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookOutbox_DeliveredAt_AvailableAt_LockedUntil",
                table: "WebhookOutbox",
                columns: new[] { "DeliveredAt", "AvailableAt", "LockedUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookOutbox_EventId",
                table: "WebhookOutbox",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebhookOutbox_ExternalClaimId",
                table: "WebhookOutbox",
                column: "ExternalClaimId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WebhookOutbox");

            migrationBuilder.DropTable(
                name: "Claims");
        }
    }
}
