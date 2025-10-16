using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SubathonManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSubathonEventAndValue2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_SubathonValue",
                table: "SubathonValue");

            migrationBuilder.DropPrimaryKey(
                name: "PK_SubathonEvent",
                table: "SubathonEvent");

            migrationBuilder.RenameTable(
                name: "SubathonValue",
                newName: "SubathonValues");

            migrationBuilder.RenameTable(
                name: "SubathonEvent",
                newName: "SubathonEvents");

            migrationBuilder.AddPrimaryKey(
                name: "PK_SubathonValues",
                table: "SubathonValues",
                columns: new[] { "EventType", "Meta" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_SubathonEvents",
                table: "SubathonEvents",
                columns: new[] { "Id", "Source" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_SubathonValues",
                table: "SubathonValues");

            migrationBuilder.DropPrimaryKey(
                name: "PK_SubathonEvents",
                table: "SubathonEvents");

            migrationBuilder.RenameTable(
                name: "SubathonValues",
                newName: "SubathonValue");

            migrationBuilder.RenameTable(
                name: "SubathonEvents",
                newName: "SubathonEvent");

            migrationBuilder.AddPrimaryKey(
                name: "PK_SubathonValue",
                table: "SubathonValue",
                columns: new[] { "EventType", "Meta" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_SubathonEvent",
                table: "SubathonEvent",
                columns: new[] { "Id", "Source" });
        }
    }
}
