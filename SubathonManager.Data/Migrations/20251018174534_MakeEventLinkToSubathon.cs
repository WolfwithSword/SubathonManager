using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SubathonManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class MakeEventLinkToSubathon : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SubathonId",
                table: "SubathonEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubathonEvents_SubathonId",
                table: "SubathonEvents",
                column: "SubathonId");

            migrationBuilder.AddForeignKey(
                name: "FK_SubathonEvents_SubathonDatas_SubathonId",
                table: "SubathonEvents",
                column: "SubathonId",
                principalTable: "SubathonDatas",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SubathonEvents_SubathonDatas_SubathonId",
                table: "SubathonEvents");

            migrationBuilder.DropIndex(
                name: "IX_SubathonEvents_SubathonId",
                table: "SubathonEvents");

            migrationBuilder.DropColumn(
                name: "SubathonId",
                table: "SubathonEvents");
        }
    }
}
