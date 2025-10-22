using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SubathonManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class separateMultiplierTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Multiplier",
                table: "SubathonEvents",
                newName: "MultiplierSeconds");

            migrationBuilder.AddColumn<double>(
                name: "MultiplierPoints",
                table: "SubathonEvents",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MultiplierPoints",
                table: "SubathonEvents");

            migrationBuilder.RenameColumn(
                name: "MultiplierSeconds",
                table: "SubathonEvents",
                newName: "Multiplier");
        }
    }
}
