using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SubathonManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AdjustPrompts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FilterEventType",
                table: "SubathonPrompts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FilterMeta",
                table: "SubathonPrompts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FilterSubType",
                table: "SubathonPrompts",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FilterEventType",
                table: "SubathonPrompts");

            migrationBuilder.DropColumn(
                name: "FilterMeta",
                table: "SubathonPrompts");

            migrationBuilder.DropColumn(
                name: "FilterSubType",
                table: "SubathonPrompts");
        }
    }
}
