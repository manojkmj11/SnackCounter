using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace API.SnackCounter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ExternalHrHandoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ExternalHrClaimId",
                table: "ReimbursementClaims",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExternalHrSubmittedAt",
                table: "ReimbursementClaims",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProcessedHrWebhooks",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessedHrWebhooks", x => x.EventId);
                    table.ForeignKey(
                        name: "FK_ProcessedHrWebhooks_ReimbursementClaims_ClaimId",
                        column: x => x.ClaimId,
                        principalTable: "ReimbursementClaims",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReimbursementClaims_ExternalHrClaimId",
                table: "ReimbursementClaims",
                column: "ExternalHrClaimId",
                unique: true,
                filter: "[ExternalHrClaimId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedHrWebhooks_ClaimId_ReceivedAt",
                table: "ProcessedHrWebhooks",
                columns: new[] { "ClaimId", "ReceivedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProcessedHrWebhooks");

            migrationBuilder.DropIndex(
                name: "IX_ReimbursementClaims_ExternalHrClaimId",
                table: "ReimbursementClaims");

            migrationBuilder.DropColumn(
                name: "ExternalHrClaimId",
                table: "ReimbursementClaims");

            migrationBuilder.DropColumn(
                name: "ExternalHrSubmittedAt",
                table: "ReimbursementClaims");
        }
    }
}
