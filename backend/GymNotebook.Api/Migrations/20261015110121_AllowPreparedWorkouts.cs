using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GymNotebook.Api.Migrations
{
    /// <inheritdoc />
    public partial class AllowPreparedWorkouts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "started_at",
                table: "workouts",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<DateOnly>(
                name: "date",
                table: "workouts",
                type: "date",
                nullable: true,
                oldClrType: typeof(DateOnly),
                oldType: "date");

            migrationBuilder.AddCheckConstraint(
                name: "ck_workouts_ended_requires_start",
                table: "workouts",
                sql: "ended_at IS NULL OR started_at IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_workouts_started_pair",
                table: "workouts",
                sql: "(date IS NULL) = (started_at IS NULL)");
        }

        /// <inheritdoc />
        // Hand-edited: EF generated a default of 0001-01-01 for both columns, which would
        // quietly give every prepared page a made-up date. Without it, rolling back fails
        // while prepared pages exist, and someone decides what to do with them first.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_workouts_ended_requires_start",
                table: "workouts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_workouts_started_pair",
                table: "workouts");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "started_at",
                table: "workouts",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateOnly>(
                name: "date",
                table: "workouts",
                type: "date",
                nullable: false,
                oldClrType: typeof(DateOnly),
                oldType: "date",
                oldNullable: true);
        }
    }
}
