using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SubathonManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class addJuniperTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JuniperStores",
                columns: table => new
                {
                    RowId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StoreName = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastFetched = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JuniperStores", x => x.RowId);
                });

            migrationBuilder.CreateTable(
                name: "JuniperProducts",
                columns: table => new
                {
                    ProductId = table.Column<string>(type: "TEXT", nullable: false),
                    ProductName = table.Column<string>(type: "TEXT", nullable: false),
                    StoreId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LastFetched = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Valid = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JuniperProducts", x => x.ProductId);
                    table.ForeignKey(
                        name: "FK_JuniperProducts_JuniperStores_StoreId",
                        column: x => x.StoreId,
                        principalTable: "JuniperStores",
                        principalColumn: "RowId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JuniperProducts_StoreId",
                table: "JuniperProducts",
                column: "StoreId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JuniperProducts");

            migrationBuilder.DropTable(
                name: "JuniperStores");
        }
    }
}
