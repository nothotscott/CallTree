using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CallTree.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    From = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    To = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Body = table.Column<string>(type: "TEXT", maxLength: 1600, nullable: false),
                    MediaCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ProviderMessageId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ReceivedAt = table.Column<string>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<string>(type: "TEXT", nullable: true),
                    FailureReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Relay",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Recipient = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Body = table.Column<string>(type: "TEXT", maxLength: 1600, nullable: false),
                    ProviderMessageId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    SentAt = table.Column<string>(type: "TEXT", nullable: true),
                    Delivery = table.Column<string>(type: "TEXT", nullable: false),
                    DeliveryChangedAt = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    MessageId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Relay", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Relay_Messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "Messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ProviderMessageId",
                table: "Messages",
                column: "ProviderMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ReceivedAt",
                table: "Messages",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_Source",
                table: "Messages",
                column: "Source");

            migrationBuilder.CreateIndex(
                name: "IX_Relay_MessageId",
                table: "Relay",
                column: "MessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Relay_ProviderMessageId",
                table: "Relay",
                column: "ProviderMessageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Relay");

            migrationBuilder.DropTable(
                name: "Messages");
        }
    }
}
