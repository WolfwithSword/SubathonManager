using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SubathonManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class updateGAP : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_GoAffProStores",
                table: "GoAffProStores");

            migrationBuilder.AlterColumn<int>(
                name: "RowId",
                table: "GoAffProStores",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER")
                .Annotation("Sqlite:Autoincrement", true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_GoAffProStores",
                table: "GoAffProStores",
                column: "RowId");

            migrationBuilder.CreateIndex(
                name: "IX_GoAffProStores_SiteId",
                table: "GoAffProStores",
                column: "SiteId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_GoAffProStores",
                table: "GoAffProStores");

            migrationBuilder.DropIndex(
                name: "IX_GoAffProStores_SiteId",
                table: "GoAffProStores");

            migrationBuilder.AlterColumn<int>(
                name: "RowId",
                table: "GoAffProStores",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER")
                .OldAnnotation("Sqlite:Autoincrement", true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_GoAffProStores",
                table: "GoAffProStores",
                columns: new[] { "SiteId", "StoreName" });
        }
    }
}
