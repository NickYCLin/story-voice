using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StoryVoice.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddListeningProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "listening_progress",
                columns: table => new
                {
                    NarrationJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    PositionMs = table.Column<long>(type: "bigint", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_listening_progress", x => new { x.OwnerId, x.NarrationJobId });
                    table.CheckConstraint("CK_listening_progress_position", "\"PositionMs\" >= 0 AND \"PositionMs\" <= \"DurationMs\" AND \"DurationMs\" > 0 AND \"DurationMs\" <= 2592000000");
                    table.ForeignKey(
                        name: "FK_listening_progress_narration_jobs_NarrationJobId",
                        column: x => x.NarrationJobId,
                        principalTable: "narration_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_listening_progress_NarrationJobId",
                table: "listening_progress",
                column: "NarrationJobId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "listening_progress");
        }
    }
}
