using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SubathonManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class addJsVarDescriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "JsVariables",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Description",
                table: "JsVariables");
        }
    }
}
