using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace API.SnackCounter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InventoryLeaveAndRefillRotation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RequesterEmployeeId",
                table: "Refills",
                type: "nvarchar(450)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SnackItemId",
                table: "Refills",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EmployeeLeaves",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Office = table.Column<int>(type: "int", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployeeLeaves", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmployeeLeaves_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmployeeLeaves_Offices_Office",
                        column: x => x.Office,
                        principalTable: "Offices",
                        principalColumn: "Location",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RefillAssignmentAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RefillId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    LocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    AttemptNumber = table.Column<int>(type: "int", nullable: false),
                    Outcome = table.Column<int>(type: "int", nullable: false),
                    WorkingMode = table.Column<int>(type: "int", nullable: true),
                    WorkingOffice = table.Column<int>(type: "int", nullable: true),
                    AttemptedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AssignmentExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefillAssignmentAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RefillAssignmentAttempts_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RefillAssignmentAttempts_Offices_WorkingOffice",
                        column: x => x.WorkingOffice,
                        principalTable: "Offices",
                        principalColumn: "Location",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RefillAssignmentAttempts_Refills_RefillId",
                        column: x => x.RefillId,
                        principalTable: "Refills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RefillRotations",
                columns: table => new
                {
                    Office = table.Column<int>(type: "int", nullable: false),
                    LastEmployeeId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    LastAttemptedLocalDate = table.Column<DateOnly>(type: "date", nullable: true),
                    LastSelectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefillRotations", x => x.Office);
                    table.ForeignKey(
                        name: "FK_RefillRotations_Employees_LastEmployeeId",
                        column: x => x.LastEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RefillRotations_Offices_Office",
                        column: x => x.Office,
                        principalTable: "Offices",
                        principalColumn: "Location",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SnackItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Office = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    QuantityOnHand = table.Column<decimal>(type: "decimal(19,3)", precision: 19, scale: 3, nullable: false),
                    ReorderThreshold = table.Column<decimal>(type: "decimal(19,3)", precision: 19, scale: 3, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    IsClaimed = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SnackItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SnackItems_Offices_Office",
                        column: x => x.Office,
                        principalTable: "Offices",
                        principalColumn: "Location",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockMovements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SnackItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(19,3)", precision: 19, scale: 3, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ActorEmployeeId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    RefillId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UnitCost = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    CostCurrency = table.Column<int>(type: "int", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockMovements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StockMovements_Employees_ActorEmployeeId",
                        column: x => x.ActorEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockMovements_Refills_RefillId",
                        column: x => x.RefillId,
                        principalTable: "Refills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockMovements_SnackItems_SnackItemId",
                        column: x => x.SnackItemId,
                        principalTable: "SnackItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Refills_Office_SnackItemId",
                table: "Refills",
                columns: new[] { "Office", "SnackItemId" },
                unique: true,
                filter: "[SnackItemId] IS NOT NULL AND [Status] IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "IX_Refills_RequesterEmployeeId",
                table: "Refills",
                column: "RequesterEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_Refills_SnackItemId",
                table: "Refills",
                column: "SnackItemId");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeLeaves_EmployeeId_StartDate_EndDate",
                table: "EmployeeLeaves",
                columns: new[] { "EmployeeId", "StartDate", "EndDate" });

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeLeaves_Office",
                table: "EmployeeLeaves",
                column: "Office");

            migrationBuilder.CreateIndex(
                name: "IX_RefillAssignmentAttempts_EmployeeId",
                table: "RefillAssignmentAttempts",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_RefillAssignmentAttempts_RefillId_EmployeeId_LocalDate",
                table: "RefillAssignmentAttempts",
                columns: new[] { "RefillId", "EmployeeId", "LocalDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefillAssignmentAttempts_RefillId_LocalDate_AttemptNumber",
                table: "RefillAssignmentAttempts",
                columns: new[] { "RefillId", "LocalDate", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefillAssignmentAttempts_WorkingOffice",
                table: "RefillAssignmentAttempts",
                column: "WorkingOffice");

            migrationBuilder.CreateIndex(
                name: "IX_RefillRotations_LastEmployeeId",
                table: "RefillRotations",
                column: "LastEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_SnackItems_Office_Name",
                table: "SnackItems",
                columns: new[] { "Office", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_ActorEmployeeId",
                table: "StockMovements",
                column: "ActorEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_RefillId",
                table: "StockMovements",
                column: "RefillId");

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_SnackItemId_OccurredAt",
                table: "StockMovements",
                columns: new[] { "SnackItemId", "OccurredAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_Refills_Employees_RequesterEmployeeId",
                table: "Refills",
                column: "RequesterEmployeeId",
                principalTable: "Employees",
                principalColumn: "EmployeeId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Refills_SnackItems_SnackItemId",
                table: "Refills",
                column: "SnackItemId",
                principalTable: "SnackItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Refills_Employees_RequesterEmployeeId",
                table: "Refills");

            migrationBuilder.DropForeignKey(
                name: "FK_Refills_SnackItems_SnackItemId",
                table: "Refills");

            migrationBuilder.DropTable(
                name: "EmployeeLeaves");

            migrationBuilder.DropTable(
                name: "RefillAssignmentAttempts");

            migrationBuilder.DropTable(
                name: "RefillRotations");

            migrationBuilder.DropTable(
                name: "StockMovements");

            migrationBuilder.DropTable(
                name: "SnackItems");

            migrationBuilder.DropIndex(
                name: "IX_Refills_Office_SnackItemId",
                table: "Refills");

            migrationBuilder.DropIndex(
                name: "IX_Refills_RequesterEmployeeId",
                table: "Refills");

            migrationBuilder.DropIndex(
                name: "IX_Refills_SnackItemId",
                table: "Refills");

            migrationBuilder.DropColumn(
                name: "RequesterEmployeeId",
                table: "Refills");

            migrationBuilder.DropColumn(
                name: "SnackItemId",
                table: "Refills");
        }
    }
}
