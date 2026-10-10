using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SubathonManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class addActionsToPromptRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CustomActionId",
                table: "SubathonPrompts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ActionId",
                table: "SubathonPromptRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActionProgress",
                table: "SubathonPromptRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ActionStatus",
                table: "SubathonPromptRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CustomActionId",
                table: "SubathonPrompts");

            migrationBuilder.DropColumn(
                name: "ActionId",
                table: "SubathonPromptRuns");

            migrationBuilder.DropColumn(
                name: "ActionProgress",
                table: "SubathonPromptRuns");

            migrationBuilder.DropColumn(
                name: "ActionStatus",
                table: "SubathonPromptRuns");
        }
    }
}
