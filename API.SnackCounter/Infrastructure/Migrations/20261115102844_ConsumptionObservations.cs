using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace API.SnackCounter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ConsumptionObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ReportedConsumedQuantity",
                table: "StockMovements",
                type: "decimal(19,3)",
                precision: 19,
                scale: 3,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReportedConsumedQuantity",
                table: "StockMovements");
        }
    }
}
