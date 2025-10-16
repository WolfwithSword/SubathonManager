using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SubathonManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSubathonEventAndValue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SubathonEvent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    EventTimestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CurrentTime = table.Column<int>(type: "INTEGER", nullable: false),
                    EventType = table.Column<int>(type: "INTEGER", nullable: true),
                    SecondsValue = table.Column<int>(type: "INTEGER", nullable: true),
                    User = table.Column<string>(type: "TEXT", nullable: true),
                    Value = table.Column<double>(type: "REAL", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", nullable: true),
                    Multiplier = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubathonEvent", x => new { x.Id, x.Source });
                });

            migrationBuilder.CreateTable(
                name: "SubathonValue",
                columns: table => new
                {
                    EventType = table.Column<int>(type: "INTEGER", nullable: false),
                    Meta = table.Column<string>(type: "TEXT", nullable: false),
                    Seconds = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubathonValue", x => new { x.EventType, x.Meta });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SubathonEvent");

            migrationBuilder.DropTable(
                name: "SubathonValue");
        }
    }
}
