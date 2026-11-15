using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace API.SnackCounter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ChatAndDurableNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    EmployeeId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    Office = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversations", x => x.Id);
                    table.CheckConstraint("CK_Conversations_Audience", "([Kind] = 0 AND [EmployeeId] IS NOT NULL AND [Office] IS NULL) OR ([Kind] = 1 AND [EmployeeId] IS NULL AND [Office] IS NOT NULL) OR ([Kind] = 2 AND [EmployeeId] IS NULL AND [Office] IS NULL)");
                    table.ForeignKey(
                        name: "FK_Conversations_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Conversations_Offices_Office",
                        column: x => x.Office,
                        principalTable: "Offices",
                        principalColumn: "Location",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConversationMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SenderEmployeeId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    RecipientEmployeeId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    Audience = table.Column<int>(type: "int", nullable: false),
                    Office = table.Column<int>(type: "int", nullable: true),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationMessages", x => x.Id);
                    table.CheckConstraint("CK_ConversationMessages_Audience", "([Audience] = 0 AND [RecipientEmployeeId] IS NOT NULL AND [Office] IS NULL) OR ([Audience] = 1 AND [RecipientEmployeeId] IS NULL AND [Office] IS NOT NULL) OR ([Audience] = 2 AND [RecipientEmployeeId] IS NULL AND [Office] IS NULL)");
                    table.ForeignKey(
                        name: "FK_ConversationMessages_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConversationMessages_Employees_RecipientEmployeeId",
                        column: x => x.RecipientEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConversationMessages_Employees_SenderEmployeeId",
                        column: x => x.SenderEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConversationMessages_Offices_Office",
                        column: x => x.Office,
                        principalTable: "Offices",
                        principalColumn: "Location",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipientEmployeeId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    Office = table.Column<int>(type: "int", nullable: true),
                    IsHrAudience = table.Column<bool>(type: "bit", nullable: false),
                    ConversationMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EventType = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DeduplicationKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DeliveryState = table.Column<int>(type: "int", nullable: false),
                    DeliveryAttempts = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReadAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                    table.CheckConstraint("CK_Notifications_Audience", "([RecipientEmployeeId] IS NOT NULL AND [Office] IS NULL AND [IsHrAudience] = 0) OR ([RecipientEmployeeId] IS NULL AND [Office] IS NOT NULL AND [IsHrAudience] = 0) OR ([RecipientEmployeeId] IS NULL AND [Office] IS NULL AND [IsHrAudience] = 1)");
                    table.ForeignKey(
                        name: "FK_Notifications_ConversationMessages_ConversationMessageId",
                        column: x => x.ConversationMessageId,
                        principalTable: "ConversationMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Notifications_Employees_RecipientEmployeeId",
                        column: x => x.RecipientEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Notifications_Offices_Office",
                        column: x => x.Office,
                        principalTable: "Offices",
                        principalColumn: "Location",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMessages_ConversationId_CreatedAt_Id",
                table: "ConversationMessages",
                columns: new[] { "ConversationId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMessages_Office",
                table: "ConversationMessages",
                column: "Office");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMessages_RecipientEmployeeId",
                table: "ConversationMessages",
                column: "RecipientEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMessages_SenderEmployeeId",
                table: "ConversationMessages",
                column: "SenderEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_EmployeeId",
                table: "Conversations",
                column: "EmployeeId",
                unique: true,
                filter: "[Kind] = 0 AND [EmployeeId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_Kind",
                table: "Conversations",
                column: "Kind",
                unique: true,
                filter: "[Kind] = 2");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_Office",
                table: "Conversations",
                column: "Office",
                unique: true,
                filter: "[Kind] = 1 AND [Office] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_ConversationMessageId",
                table: "Notifications",
                column: "ConversationMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_DeduplicationKey",
                table: "Notifications",
                column: "DeduplicationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_Office_CreatedAt",
                table: "Notifications",
                columns: new[] { "Office", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_RecipientEmployeeId_CreatedAt",
                table: "Notifications",
                columns: new[] { "RecipientEmployeeId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "ConversationMessages");

            migrationBuilder.DropTable(
                name: "Conversations");
        }
    }
}
