using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SubathonManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class changeMultiplierStruct : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Multiplier",
                table: "SubathonDatas");

            migrationBuilder.CreateTable(
                name: "MultiplierDatas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Multiplier = table.Column<double>(type: "REAL", nullable: false),
                    Duration = table.Column<TimeSpan>(type: "TEXT", nullable: true),
                    Started = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SubathonId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ApplyToSeconds = table.Column<bool>(type: "INTEGER", nullable: false),
                    ApplyToPoints = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MultiplierDatas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MultiplierDatas_SubathonDatas_SubathonId",
                        column: x => x.SubathonId,
                        principalTable: "SubathonDatas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MultiplierDatas_SubathonId",
                table: "MultiplierDatas",
                column: "SubathonId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MultiplierDatas");

            migrationBuilder.AddColumn<double>(
                name: "Multiplier",
                table: "SubathonDatas",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);
        }
    }
}
