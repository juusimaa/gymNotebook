using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GymNotebook.Api.Migrations
{
    /// <inheritdoc />
    public partial class RemovePreparedWorkouts : Migration
    {
        /// <inheritdoc />
        // Hand-edited (specs/006 research R15). Prepared pages are gone, but any that exist
        // are not thrown away: each becomes a template first, then the page is deleted
        // (its blocks and sets go by the cascade). The name follows the same rule restore
        // uses for an older backup (PreparedPageTemplateName): the title, or "Plan" and the
        // UTC creation day, then " (2)", " (3)"… while the account already uses the name
        // after normalizing (lower case, runs of whitespace as one space, trimmed, as
        // ExerciseNameNormalizer does). Template sets have no status: every set on a
        // prepared page was planned anyway. EF also generated a 0001-01-01 column default
        // for both columns; removed, since no row needs one once the plans are gone.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    page record;
                    block record;
                    base_name text;
                    candidate text;
                    suffix int;
                    new_template_id int;
                    new_block_id int;
                BEGIN
                    FOR page IN
                        SELECT id, user_id, title, created_at
                        FROM workouts
                        WHERE started_at IS NULL
                        ORDER BY user_id, created_at, id
                    LOOP
                        base_name := COALESCE(
                            NULLIF(regexp_replace(page.title, '^\s+|\s+$', '', 'g'), ''),
                            'Plan ' || to_char(page.created_at AT TIME ZONE 'UTC', 'YYYY-MM-DD'));
                        candidate := base_name;
                        suffix := 1;
                        WHILE EXISTS (
                            SELECT 1 FROM templates
                            WHERE user_id = page.user_id
                              AND normalized_name = regexp_replace(
                                  regexp_replace(lower(candidate), '^\s+|\s+$', '', 'g'), '\s+', ' ', 'g'))
                        LOOP
                            suffix := suffix + 1;
                            candidate := base_name || ' (' || suffix || ')';
                        END LOOP;

                        INSERT INTO templates (user_id, name, normalized_name)
                        VALUES (page.user_id, candidate, regexp_replace(
                            regexp_replace(lower(candidate), '^\s+|\s+$', '', 'g'), '\s+', ' ', 'g'))
                        RETURNING id INTO new_template_id;

                        FOR block IN
                            SELECT id, exercise_id, position
                            FROM workout_exercises
                            WHERE workout_id = page.id
                        LOOP
                            INSERT INTO template_exercises (template_id, exercise_id, position)
                            VALUES (new_template_id, block.exercise_id, block.position)
                            RETURNING id INTO new_block_id;

                            INSERT INTO template_sets (template_exercise_id, set_number, weight, reps, is_warmup)
                            SELECT new_block_id, set_number, weight, reps, is_warmup
                            FROM set_entries
                            WHERE workout_exercise_id = block.id;
                        END LOOP;
                    END LOOP;

                    DELETE FROM workouts WHERE started_at IS NULL;
                END $$;
                """);

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

        /// <inheritdoc />
        // Allows prepared pages again; the ones converted to templates stay templates.
        protected override void Down(MigrationBuilder migrationBuilder)
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
    }
}
