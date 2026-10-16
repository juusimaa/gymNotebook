using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GymNotebook.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDemoAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "demo_expires_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_demo_expires_at",
                table: "users",
                column: "demo_expires_at",
                filter: "demo_expires_at IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_users_demo_expires_at",
                table: "users");

            migrationBuilder.DropColumn(
                name: "demo_expires_at",
                table: "users");
        }
    }
}
