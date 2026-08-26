using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StoryVoice.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalVoiceCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "external_voice_credentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConsumerKeyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    KeyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    TokenSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastUsedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReplacedByCredentialId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_voice_credentials", x => x.Id);
                    table.CheckConstraint("CK_external_voice_credentials_expiry", "\"ExpiresAtUtc\" > \"CreatedAtUtc\"");
                    table.CheckConstraint("CK_external_voice_credentials_token_sha256", "length(\"TokenSha256\") = 64");
                    table.ForeignKey(
                        name: "FK_external_voice_credentials_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_external_voice_credentials_external_voice_credentials_Repla~",
                        column: x => x.ReplacedByCredentialId,
                        principalTable: "external_voice_credentials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "external_voice_credential_audits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    CredentialId = table.Column<Guid>(type: "uuid", nullable: false),
                    CredentialKeyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Action = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RelatedCredentialId = table.Column<Guid>(type: "uuid", nullable: true),
                    RelatedCredentialKeyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_voice_credential_audits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_external_voice_credential_audits_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_external_voice_credential_audits_external_voice_credentials~",
                        column: x => x.CredentialId,
                        principalTable: "external_voice_credentials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_external_voice_credential_audits_external_voice_credential~1",
                        column: x => x.RelatedCredentialId,
                        principalTable: "external_voice_credentials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_external_voice_credential_audits_CredentialId",
                table: "external_voice_credential_audits",
                column: "CredentialId");

            migrationBuilder.CreateIndex(
                name: "IX_external_voice_credential_audits_OwnerId_OccurredAtUtc",
                table: "external_voice_credential_audits",
                columns: new[] { "OwnerId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_external_voice_credential_audits_RelatedCredentialId",
                table: "external_voice_credential_audits",
                column: "RelatedCredentialId");

            migrationBuilder.CreateIndex(
                name: "IX_external_voice_credentials_KeyId",
                table: "external_voice_credentials",
                column: "KeyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_external_voice_credentials_OwnerId_ConsumerKeyId_RevokedAtU~",
                table: "external_voice_credentials",
                columns: new[] { "OwnerId", "ConsumerKeyId", "RevokedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_external_voice_credentials_ReplacedByCredentialId",
                table: "external_voice_credentials",
                column: "ReplacedByCredentialId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "external_voice_credential_audits");

            migrationBuilder.DropTable(
                name: "external_voice_credentials");
        }
    }
}
