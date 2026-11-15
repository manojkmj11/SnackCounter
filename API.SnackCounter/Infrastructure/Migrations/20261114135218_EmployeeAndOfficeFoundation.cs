using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace API.SnackCounter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EmployeeAndOfficeFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OfficeVolunteerRoster_Volunteers_EmployeeId",
                table: "OfficeVolunteerRoster");

            migrationBuilder.DropForeignKey(
                name: "FK_Refills_Volunteers_AssignedVolunteerEmployeeId",
                table: "Refills");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Volunteers",
                table: "Volunteers");

            migrationBuilder.DropPrimaryKey(
                name: "PK_OfficeVolunteerRoster",
                table: "OfficeVolunteerRoster");

            migrationBuilder.RenameTable(
                name: "Volunteers",
                newName: "Employees");

            migrationBuilder.RenameTable(
                name: "OfficeVolunteerRoster",
                newName: "EmployeeOffices");

            migrationBuilder.RenameIndex(
                name: "IX_OfficeVolunteerRoster_EmployeeId",
                table: "EmployeeOffices",
                newName: "IX_EmployeeOffices_EmployeeId");

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "Refills",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "Employees",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "Employees",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.Sql("UPDATE [Employees] SET [DisplayName] = [EmployeeId] WHERE [DisplayName] = N'';");

            migrationBuilder.AddColumn<DateOnly>(
                name: "ActiveFrom",
                table: "EmployeeOffices",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ActiveUntil",
                table: "EmployeeOffices",
                type: "date",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_Employees",
                table: "Employees",
                column: "EmployeeId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_EmployeeOffices",
                table: "EmployeeOffices",
                columns: new[] { "Office", "EmployeeId" });

            migrationBuilder.CreateTable(
                name: "Offices",
                columns: table => new
                {
                    Location = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TimeZoneId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    BudgetCurrency = table.Column<int>(type: "int", nullable: false),
                    MonthlyBudget = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    WarningThresholdPercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Offices", x => x.Location);
                });

            migrationBuilder.InsertData(
                table: "Offices",
                columns: new[] { "Location", "BudgetCurrency", "IsActive", "MonthlyBudget", "Name", "TimeZoneId", "WarningThresholdPercent" },
                values: new object[,]
                {
                    { 0, 0, true, 6000m, "Oslo", "Europe/Oslo", 80m },
                    { 1, 1, true, 15000m, "Bengaluru", "Asia/Kolkata", 80m }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Employees_HomeOffice",
                table: "Employees",
                column: "HomeOffice");

            migrationBuilder.AddForeignKey(
                name: "FK_EmployeeOffices_Employees_EmployeeId",
                table: "EmployeeOffices",
                column: "EmployeeId",
                principalTable: "Employees",
                principalColumn: "EmployeeId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_EmployeeOffices_Offices_Office",
                table: "EmployeeOffices",
                column: "Office",
                principalTable: "Offices",
                principalColumn: "Location",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Employees_Offices_HomeOffice",
                table: "Employees",
                column: "HomeOffice",
                principalTable: "Offices",
                principalColumn: "Location",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Refills_Employees_AssignedVolunteerEmployeeId",
                table: "Refills",
                column: "AssignedVolunteerEmployeeId",
                principalTable: "Employees",
                principalColumn: "EmployeeId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Refills_Offices_Office",
                table: "Refills",
                column: "Office",
                principalTable: "Offices",
                principalColumn: "Location",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_EmployeeOffices_Employees_EmployeeId",
                table: "EmployeeOffices");

            migrationBuilder.DropForeignKey(
                name: "FK_EmployeeOffices_Offices_Office",
                table: "EmployeeOffices");

            migrationBuilder.DropForeignKey(
                name: "FK_Employees_Offices_HomeOffice",
                table: "Employees");

            migrationBuilder.DropForeignKey(
                name: "FK_Refills_Employees_AssignedVolunteerEmployeeId",
                table: "Refills");

            migrationBuilder.DropForeignKey(
                name: "FK_Refills_Offices_Office",
                table: "Refills");

            migrationBuilder.DropTable(
                name: "Offices");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Employees",
                table: "Employees");

            migrationBuilder.DropIndex(
                name: "IX_Employees_HomeOffice",
                table: "Employees");

            migrationBuilder.DropPrimaryKey(
                name: "PK_EmployeeOffices",
                table: "EmployeeOffices");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "Refills");

            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "ActiveFrom",
                table: "EmployeeOffices");

            migrationBuilder.DropColumn(
                name: "ActiveUntil",
                table: "EmployeeOffices");

            migrationBuilder.RenameTable(
                name: "Employees",
                newName: "Volunteers");

            migrationBuilder.RenameTable(
                name: "EmployeeOffices",
                newName: "OfficeVolunteerRoster");

            migrationBuilder.RenameIndex(
                name: "IX_EmployeeOffices_EmployeeId",
                table: "OfficeVolunteerRoster",
                newName: "IX_OfficeVolunteerRoster_EmployeeId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Volunteers",
                table: "Volunteers",
                column: "EmployeeId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_OfficeVolunteerRoster",
                table: "OfficeVolunteerRoster",
                columns: new[] { "Office", "EmployeeId" });

            migrationBuilder.AddForeignKey(
                name: "FK_OfficeVolunteerRoster_Volunteers_EmployeeId",
                table: "OfficeVolunteerRoster",
                column: "EmployeeId",
                principalTable: "Volunteers",
                principalColumn: "EmployeeId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Refills_Volunteers_AssignedVolunteerEmployeeId",
                table: "Refills",
                column: "AssignedVolunteerEmployeeId",
                principalTable: "Volunteers",
                principalColumn: "EmployeeId",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
