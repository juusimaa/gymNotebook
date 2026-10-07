using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GymNotebook.Api.Migrations
{
    // Email sign-in (specs/002 PR 3, data-model.md). Hand-edited after generation: EF's
    // guess was to rename username → email and add display_name with DEFAULT '', which is
    // the opposite of the design and would also have run happily on a table full of users.
    //
    // What it does instead:
    //   - username is renamed display_name (owner decision O1), keeping existing values,
    //     and loses its unique index — display names aren't unique any more.
    //   - email is added NOT NULL with *no default*. PostgreSQL refuses that on a table
    //     that has rows, so if the operator wipe (quickstart.md) was forgotten, this
    //     migration fails and the deploy stops, instead of inventing addresses (plan D11,
    //     FR-022). UserEmailMigrationTests checks exactly that.
    /// <inheritdoc />
    public partial class AddUserEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_users_username",
                table: "users");

            migrationBuilder.RenameColumn(
                name: "username",
                table: "users",
                newName: "display_name");

            migrationBuilder.AddColumn<string>(
                name: "email",
                table: "users",
                type: "text",
                nullable: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "email_verified_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_email",
                table: "users",
                column: "email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_users_email",
                table: "users");

            migrationBuilder.DropColumn(
                name: "email_verified_at",
                table: "users");

            migrationBuilder.DropColumn(
                name: "email",
                table: "users");

            migrationBuilder.RenameColumn(
                name: "display_name",
                table: "users",
                newName: "username");

            migrationBuilder.CreateIndex(
                name: "ix_users_username",
                table: "users",
                column: "username",
                unique: true);
        }
    }
}
