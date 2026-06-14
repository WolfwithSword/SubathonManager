using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SubathonManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWheelSpin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WheelSets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    SpinCount = table.Column<int>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WheelSets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WheelItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    Weight = table.Column<int>(type: "INTEGER", nullable: false),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    IsInfinite = table.Column<bool>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Index = table.Column<int>(type: "INTEGER", nullable: false),
                    WheelId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WheelItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WheelItems_WheelSets_WheelId",
                        column: x => x.WheelId,
                        principalTable: "WheelSets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WheelSpinActions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActionType = table.Column<int>(type: "INTEGER", nullable: false),
                    Parameter = table.Column<string>(type: "TEXT", nullable: false),
                    WheelItemId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WheelSpinActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WheelSpinActions_WheelItems_WheelItemId",
                        column: x => x.WheelItemId,
                        principalTable: "WheelItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WheelSpinHistories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WheelId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WheelItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WheelSpinHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WheelSpinHistories_WheelItems_WheelItemId",
                        column: x => x.WheelItemId,
                        principalTable: "WheelItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WheelSpinHistories_WheelSets_WheelId",
                        column: x => x.WheelId,
                        principalTable: "WheelSets",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_WheelItems_WheelId",
                table: "WheelItems",
                column: "WheelId");

            migrationBuilder.CreateIndex(
                name: "IX_WheelSpinActions_WheelItemId",
                table: "WheelSpinActions",
                column: "WheelItemId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WheelSpinHistories_WheelId",
                table: "WheelSpinHistories",
                column: "WheelId");

            migrationBuilder.CreateIndex(
                name: "IX_WheelSpinHistories_WheelItemId",
                table: "WheelSpinHistories",
                column: "WheelItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WheelSpinActions");

            migrationBuilder.DropTable(
                name: "WheelSpinHistories");

            migrationBuilder.DropTable(
                name: "WheelItems");

            migrationBuilder.DropTable(
                name: "WheelSets");
        }
    }
}
