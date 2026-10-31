using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StoryVoice.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PreserveNarrationTracks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_rebuild_batches_draft_cast",
                table: "series_cast_rebuild_batches");

            migrationBuilder.CreateTable(
                name: "narration_track_selections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    BookId = table.Column<Guid>(type: "uuid", nullable: false),
                    NarrationJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreviousNarrationJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_narration_track_selections", x => x.Id);
                    table.CheckConstraint("CK_track_selection_revision", "\"Revision\" > 0");
                    table.ForeignKey(
                        name: "FK_narration_track_selections_narration_jobs_OwnerId_Narration~",
                        columns: x => new { x.OwnerId, x.NarrationJobId },
                        principalTable: "narration_jobs",
                        principalColumns: new[] { "OwnerId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_rebuild_batches_draft_cast",
                table: "series_cast_rebuild_batches",
                columns: new[] { "OwnerId", "SeriesId", "DraftCastRevisionId" },
                unique: true,
                filter: "\"Status\" <> 'Failed'");

            migrationBuilder.CreateIndex(
                name: "IX_narration_track_selections_OwnerId_BookId_Revision",
                table: "narration_track_selections",
                columns: new[] { "OwnerId", "BookId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_narration_track_selections_OwnerId_NarrationJobId",
                table: "narration_track_selections",
                columns: new[] { "OwnerId", "NarrationJobId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Do not erase retained failed cohorts merely to satisfy the old unique index.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (
                        SELECT 1 FROM series_cast_rebuild_batches
                        GROUP BY "OwnerId", "SeriesId", "DraftCastRevisionId" HAVING COUNT(*) > 1
                    ) THEN
                        RAISE EXCEPTION 'Cannot downgrade: retained rebuild history requires the new schema';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropTable(
                name: "narration_track_selections");

            migrationBuilder.DropIndex(
                name: "UX_rebuild_batches_draft_cast",
                table: "series_cast_rebuild_batches");

            migrationBuilder.CreateIndex(
                name: "UX_rebuild_batches_draft_cast",
                table: "series_cast_rebuild_batches",
                columns: new[] { "OwnerId", "SeriesId", "DraftCastRevisionId" },
                unique: true);
        }
    }
}
