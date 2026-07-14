using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CallTree.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Calls",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    SourceClassification = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    AnsweredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    EndedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    TerminationReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Calls", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CallLeg",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Direction = table.Column<string>(type: "TEXT", nullable: false),
                    RemoteNumber = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    RawCallerId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SipCallId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    AnsweredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    EndedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    HangupInitiator = table.Column<string>(type: "TEXT", nullable: true),
                    CallId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CallLeg", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CallLeg_Calls_CallId",
                        column: x => x.CallId,
                        principalTable: "Calls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Recording",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    ChannelLayout = table.Column<string>(type: "TEXT", nullable: false),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    FinalizedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CallId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Recording", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Recording_Calls_CallId",
                        column: x => x.CallId,
                        principalTable: "Calls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CallLeg_CallId",
                table: "CallLeg",
                column: "CallId");

            migrationBuilder.CreateIndex(
                name: "IX_Calls_Source",
                table: "Calls",
                column: "Source");

            migrationBuilder.CreateIndex(
                name: "IX_Calls_StartedAt",
                table: "Calls",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Recording_CallId",
                table: "Recording",
                column: "CallId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CallLeg");

            migrationBuilder.DropTable(
                name: "Recording");

            migrationBuilder.DropTable(
                name: "Calls");
        }
    }
}
