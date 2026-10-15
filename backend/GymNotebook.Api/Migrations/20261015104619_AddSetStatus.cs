using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GymNotebook.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSetStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "set_entries",
                type: "text",
                nullable: false,
                defaultValue: "done");

            migrationBuilder.AddCheckConstraint(
                name: "ck_set_entries_status",
                table: "set_entries",
                sql: "status IN ('planned', 'done', 'skipped')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_set_entries_status",
                table: "set_entries");

            migrationBuilder.DropColumn(
                name: "status",
                table: "set_entries");
        }
    }
}
