using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SubathonManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class addDonothonValues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Type",
                table: "SubathonGoalSets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Money",
                table: "SubathonGoals",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "SubathonDatas",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MoneySum",
                table: "SubathonDatas",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Type",
                table: "SubathonGoalSets");

            migrationBuilder.DropColumn(
                name: "Money",
                table: "SubathonGoals");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "SubathonDatas");

            migrationBuilder.DropColumn(
                name: "MoneySum",
                table: "SubathonDatas");
        }
    }
}
