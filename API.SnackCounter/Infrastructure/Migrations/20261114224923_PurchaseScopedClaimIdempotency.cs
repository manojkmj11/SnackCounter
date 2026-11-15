using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace API.SnackCounter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PurchaseScopedClaimIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReimbursementClaims_IdempotencyKey",
                table: "ReimbursementClaims");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ReimbursementClaims_IdempotencyKey",
                table: "ReimbursementClaims",
                column: "IdempotencyKey",
                unique: true);
        }
    }
}
