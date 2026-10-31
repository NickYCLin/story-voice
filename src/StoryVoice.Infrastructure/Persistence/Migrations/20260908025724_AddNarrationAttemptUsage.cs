using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StoryVoice.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNarrationAttemptUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_narration_jobs_OwnerId_Id",
                table: "narration_jobs",
                columns: new[] { "OwnerId", "Id" });

            migrationBuilder.CreateTable(
                name: "narration_attempt_usage",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    NarrationJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseOwner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Provider = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    InputCharacters = table.Column<long>(type: "bigint", nullable: true),
                    CompletedChunks = table.Column<int>(type: "integer", nullable: true),
                    TotalChunks = table.Column<int>(type: "integer", nullable: true),
                    ElapsedMs = table.Column<long>(type: "bigint", nullable: true),
                    SynthesisElapsedMs = table.Column<long>(type: "bigint", nullable: true),
                    AudioBytes = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_narration_attempt_usage", x => x.Id);
                    table.CheckConstraint("CK_narration_usage_outcome", "\"Outcome\" IN ('Running', 'Completed', 'Failed', 'TimedOut', 'Cancelled', 'WorkerStopped', 'LeaseLost', 'Unknown')");
                    table.CheckConstraint("CK_narration_usage_quantities", "(\"InputCharacters\" IS NULL OR \"InputCharacters\" >= 0) AND (\"ElapsedMs\" IS NULL OR \"ElapsedMs\" >= 0) AND (\"SynthesisElapsedMs\" IS NULL OR \"SynthesisElapsedMs\" >= 0) AND (\"AudioBytes\" IS NULL OR \"AudioBytes\" > 0) AND ((\"CompletedChunks\" IS NULL AND \"TotalChunks\" IS NULL) OR (\"CompletedChunks\" IS NOT NULL AND \"TotalChunks\" IS NOT NULL AND \"CompletedChunks\" >= 0 AND \"TotalChunks\" > 0 AND \"CompletedChunks\" <= \"TotalChunks\"))");
                    table.ForeignKey(
                        name: "FK_narration_attempt_usage_narration_jobs_OwnerId_NarrationJob~",
                        columns: x => new { x.OwnerId, x.NarrationJobId },
                        principalTable: "narration_jobs",
                        principalColumns: new[] { "OwnerId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_narration_attempt_usage_OwnerId_NarrationJobId_StartedAt",
                table: "narration_attempt_usage",
                columns: new[] { "OwnerId", "NarrationJobId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "narration_attempt_usage");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_narration_jobs_OwnerId_Id",
                table: "narration_jobs");
        }
    }
}
