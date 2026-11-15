using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace API.SnackCounter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PurchaseLinkedReimbursementAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReimbursementClaims_EmployeeId",
                table: "ReimbursementClaims");

            migrationBuilder.AddColumn<Guid>(
                name: "PreviousClaimId",
                table: "ReimbursementClaims",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PurchaseId",
                table: "ReimbursementClaims",
                type: "uniqueidentifier",
                nullable: false,
                defaultValueSql: "NEWID()");

            migrationBuilder.AddColumn<int>(
                name: "SubmissionAttempt",
                table: "ReimbursementClaims",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "IX_ReimbursementClaims_EmployeeId_PurchaseId_SubmissionAttempt",
                table: "ReimbursementClaims",
                columns: new[] { "EmployeeId", "PurchaseId", "SubmissionAttempt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReimbursementClaims_PreviousClaimId",
                table: "ReimbursementClaims",
                column: "PreviousClaimId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ReimbursementClaims_SubmissionAttempt",
                table: "ReimbursementClaims",
                sql: "[SubmissionAttempt] >= 1");

            migrationBuilder.AddForeignKey(
                name: "FK_ReimbursementClaims_ReimbursementClaims_PreviousClaimId",
                table: "ReimbursementClaims",
                column: "PreviousClaimId",
                principalTable: "ReimbursementClaims",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ReimbursementClaims_ReimbursementClaims_PreviousClaimId",
                table: "ReimbursementClaims");

            migrationBuilder.DropIndex(
                name: "IX_ReimbursementClaims_EmployeeId_PurchaseId_SubmissionAttempt",
                table: "ReimbursementClaims");

            migrationBuilder.DropIndex(
                name: "IX_ReimbursementClaims_PreviousClaimId",
                table: "ReimbursementClaims");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ReimbursementClaims_SubmissionAttempt",
                table: "ReimbursementClaims");

            migrationBuilder.DropColumn(
                name: "PreviousClaimId",
                table: "ReimbursementClaims");

            migrationBuilder.DropColumn(
                name: "PurchaseId",
                table: "ReimbursementClaims");

            migrationBuilder.DropColumn(
                name: "SubmissionAttempt",
                table: "ReimbursementClaims");

            migrationBuilder.CreateIndex(
                name: "IX_ReimbursementClaims_EmployeeId",
                table: "ReimbursementClaims",
                column: "EmployeeId");
        }
    }
}
