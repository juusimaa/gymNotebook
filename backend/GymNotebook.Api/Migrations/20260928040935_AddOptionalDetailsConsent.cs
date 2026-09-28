using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GymNotebook.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddOptionalDetailsConsent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Reviewed as generated: two nullable columns with no default, so every
            // existing account starts without consent. Nothing is backfilled — consent is
            // never seeded (specs/001 FR-035, data-model.md → Optional-details consent).
            migrationBuilder.AddColumn<string>(
                name: "optional_details_consent_version",
                table: "users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "optional_details_consented_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_optional_details_consent_pair",
                table: "users",
                sql: "(optional_details_consent_version IS NULL) = (optional_details_consented_at IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_users_optional_details_consent_pair",
                table: "users");

            migrationBuilder.DropColumn(
                name: "optional_details_consent_version",
                table: "users");

            migrationBuilder.DropColumn(
                name: "optional_details_consented_at",
                table: "users");
        }
    }
}
