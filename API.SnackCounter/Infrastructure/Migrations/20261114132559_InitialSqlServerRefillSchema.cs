using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace API.SnackCounter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialSqlServerRefillSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Volunteers",
                columns: table => new
                {
                    EmployeeId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    HomeOffice = table.Column<int>(type: "int", nullable: false),
                    PayrollCurrency = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Volunteers", x => x.EmployeeId);
                });

            migrationBuilder.CreateTable(
                name: "OfficeVolunteerRoster",
                columns: table => new
                {
                    Office = table.Column<int>(type: "int", nullable: false),
                    EmployeeId = table.Column<string>(type: "nvarchar(450)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfficeVolunteerRoster", x => new { x.Office, x.EmployeeId });
                    table.ForeignKey(
                        name: "FK_OfficeVolunteerRoster_Volunteers_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Volunteers",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Refills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Office = table.Column<int>(type: "int", nullable: false),
                    Item = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AssignedVolunteerEmployeeId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AssignmentExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PurchaseAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    PurchaseCurrency = table.Column<int>(type: "int", nullable: true),
                    PurchasedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PurchaseDescription = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ReceiptImage = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    HrClaimId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Refills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Refills_Volunteers_AssignedVolunteerEmployeeId",
                        column: x => x.AssignedVolunteerEmployeeId,
                        principalTable: "Volunteers",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OfficeVolunteerRoster_EmployeeId",
                table: "OfficeVolunteerRoster",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_Refills_AssignedVolunteerEmployeeId",
                table: "Refills",
                column: "AssignedVolunteerEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_Refills_Office_PurchasedAt",
                table: "Refills",
                columns: new[] { "Office", "PurchasedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OfficeVolunteerRoster");

            migrationBuilder.DropTable(
                name: "Refills");

            migrationBuilder.DropTable(
                name: "Volunteers");
        }
    }
}
