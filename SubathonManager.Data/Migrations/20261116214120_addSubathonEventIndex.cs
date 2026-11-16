using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SubathonManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class addSubathonEventIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SubathonEvents_SubathonId",
                table: "SubathonEvents");

            migrationBuilder.CreateIndex(
                name: "IX_SubathonEvents_SubathonId_EventType_EventTimestamp",
                table: "SubathonEvents",
                columns: new[] { "SubathonId", "EventType", "EventTimestamp" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SubathonEvents_SubathonId_EventType_EventTimestamp",
                table: "SubathonEvents");

            migrationBuilder.CreateIndex(
                name: "IX_SubathonEvents_SubathonId",
                table: "SubathonEvents",
                column: "SubathonId");
        }
    }
}
