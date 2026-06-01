using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SubathonManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRowIdToGAP : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RowId",
                table: "GoAffProStores",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RowId",
                table: "GoAffProStores");
        }
    }
}
