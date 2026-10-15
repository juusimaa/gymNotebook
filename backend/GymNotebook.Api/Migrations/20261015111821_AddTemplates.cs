using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GymNotebook.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "templates",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    normalized_name = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_templates", x => x.id);
                    table.ForeignKey(
                        name: "fk_templates_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "template_exercises",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    template_id = table.Column<int>(type: "integer", nullable: false),
                    exercise_id = table.Column<int>(type: "integer", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_template_exercises", x => x.id);
                    table.ForeignKey(
                        name: "fk_template_exercises_exercises_exercise_id",
                        column: x => x.exercise_id,
                        principalTable: "exercises",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_template_exercises_templates_template_id",
                        column: x => x.template_id,
                        principalTable: "templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "template_sets",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    template_exercise_id = table.Column<int>(type: "integer", nullable: false),
                    set_number = table.Column<int>(type: "integer", nullable: false),
                    weight = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: true),
                    reps = table.Column<int>(type: "integer", nullable: false),
                    is_warmup = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_template_sets", x => x.id);
                    table.ForeignKey(
                        name: "fk_template_sets_template_exercises_template_exercise_id",
                        column: x => x.template_exercise_id,
                        principalTable: "template_exercises",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_template_exercises_exercise_id",
                table: "template_exercises",
                column: "exercise_id");

            migrationBuilder.CreateIndex(
                name: "ix_template_exercises_template_id",
                table: "template_exercises",
                column: "template_id");

            migrationBuilder.CreateIndex(
                name: "ix_template_sets_template_exercise_id_set_number",
                table: "template_sets",
                columns: new[] { "template_exercise_id", "set_number" });

            migrationBuilder.CreateIndex(
                name: "ix_templates_user_id_normalized_name",
                table: "templates",
                columns: new[] { "user_id", "normalized_name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "template_sets");

            migrationBuilder.DropTable(
                name: "template_exercises");

            migrationBuilder.DropTable(
                name: "templates");
        }
    }
}
