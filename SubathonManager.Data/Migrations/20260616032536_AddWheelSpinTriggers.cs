using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SubathonManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWheelSpinTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WheelSpinTriggers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    SpinsToAdd = table.Column<int>(type: "INTEGER", nullable: false),
                    EventType = table.Column<int>(type: "INTEGER", nullable: false),
                    TierValue = table.Column<string>(type: "TEXT", nullable: true),
                    CountThreshold = table.Column<int>(type: "INTEGER", nullable: true),
                    MoneyThreshold = table.Column<double>(type: "REAL", nullable: true),
                    Currency = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WheelSpinTriggers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WheelSpinTriggerHistories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TriggerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TriggeredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TriggerUser = table.Column<string>(type: "TEXT", nullable: true),
                    TriggerSource = table.Column<int>(type: "INTEGER", nullable: false),
                    SpinsAdded = table.Column<int>(type: "INTEGER", nullable: false),
                    SubathonEventId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SubathonEventType = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WheelSpinTriggerHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WheelSpinTriggerHistories_WheelSpinTriggers_TriggerId",
                        column: x => x.TriggerId,
                        principalTable: "WheelSpinTriggers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WheelSpinTriggerHistories_TriggerId",
                table: "WheelSpinTriggerHistories",
                column: "TriggerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WheelSpinTriggerHistories");

            migrationBuilder.DropTable(
                name: "WheelSpinTriggers");
        }
    }
}
