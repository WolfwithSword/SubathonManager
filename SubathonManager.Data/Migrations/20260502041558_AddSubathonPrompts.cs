using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SubathonManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSubathonPrompts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SubathonPromptSets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    Interval = table.Column<long>(type: "INTEGER", nullable: false),
                    RandomOffset = table.Column<long>(type: "INTEGER", nullable: false),
                    Cooldown = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubathonPromptSets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SubathonPrompts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletionDuration = table.Column<long>(type: "INTEGER", nullable: false),
                    IsInfinite = table.Column<bool>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    SubType = table.Column<int>(type: "INTEGER", nullable: false),
                    SetId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubathonPrompts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubathonPrompts_SubathonPromptSets_SetId",
                        column: x => x.SetId,
                        principalTable: "SubathonPromptSets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SubathonPromptRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PromptId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    SnapshotTargetValue = table.Column<long>(type: "INTEGER", nullable: false),
                    BaselineCount = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubathonPromptRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubathonPromptRuns_SubathonPromptSets_SetId",
                        column: x => x.SetId,
                        principalTable: "SubathonPromptSets",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SubathonPromptRuns_SubathonPrompts_PromptId",
                        column: x => x.PromptId,
                        principalTable: "SubathonPrompts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SubathonPromptRuns_PromptId",
                table: "SubathonPromptRuns",
                column: "PromptId");

            migrationBuilder.CreateIndex(
                name: "IX_SubathonPromptRuns_SetId",
                table: "SubathonPromptRuns",
                column: "SetId");

            migrationBuilder.CreateIndex(
                name: "IX_SubathonPrompts_SetId",
                table: "SubathonPrompts",
                column: "SetId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SubathonPromptRuns");

            migrationBuilder.DropTable(
                name: "SubathonPrompts");

            migrationBuilder.DropTable(
                name: "SubathonPromptSets");
        }
    }
}
