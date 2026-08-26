using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StoryVoice.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalVoiceUsageLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "external_voice_usage_records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConsumerKeyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CredentialKeyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProjectId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AccessTier = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RequestId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    VoiceAlias = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StatusCode = table.Column<int>(type: "integer", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DurationMilliseconds = table.Column<int>(type: "integer", nullable: false),
                    TextCharacters = table.Column<int>(type: "integer", nullable: true),
                    ResponseBytes = table.Column<long>(type: "bigint", nullable: false),
                    AudioDurationMilliseconds = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_voice_usage_records", x => x.Id);
                    table.CheckConstraint("CK_external_voice_usage_records_metrics", "\"DurationMilliseconds\" >= 0 AND \"TextCharacters\" >= 0 AND \"ResponseBytes\" >= 0 AND \"AudioDurationMilliseconds\" >= 0");
                    table.CheckConstraint("CK_external_voice_usage_records_status", "\"StatusCode\" >= 100 AND \"StatusCode\" <= 599");
                    table.ForeignKey(
                        name: "FK_external_voice_usage_records_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_external_voice_usage_records_OwnerId_OccurredAtUtc",
                table: "external_voice_usage_records",
                columns: new[] { "OwnerId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_external_voice_usage_records_OwnerId_ProjectId_OccurredAtUtc",
                table: "external_voice_usage_records",
                columns: new[] { "OwnerId", "ProjectId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_external_voice_usage_records_RequestId",
                table: "external_voice_usage_records",
                column: "RequestId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "external_voice_usage_records");
        }
    }
}
