namespace GymNotebook.Api;

// The "fieldGuide" object embedded in every export (specs/001 contracts/api.md → Export
// format version 1). The file has to explain itself to someone who has never seen this
// API: every field, its unit, what null means, how records point at each other, the order
// they come in and how dates are to be read. Kept next to the export rather than in docs/
// because it *is* part of the export: changing a field in NotebookExport without changing
// its entry here is the mistake ExportTests' field-guide check exists to catch.
//
// Anonymous objects with camelCase member names, so the serialized keys match the export's
// field names exactly.
public static class ExportFieldGuide
{
    public static readonly object Content = new
    {
        about = "A copy of one Gym Notebook account: its exercises, workouts, the exercise blocks within each workout and their sets, plus the account's privacy records. It contains no password, password hash, sign-in token or any other account's data.",
        formatVersion = "Integer. The version of this file's layout; this layout is version 1.",
        snapshotAt = "UTC instant. When the database snapshot this file was read from was taken. Every record in the file reflects the notebook at that single moment; it is not a claim that every record was created then.",
        conventions = new[]
        {
            "Instants (createdAt, startedAt, endedAt, snapshotAt, acknowledgedAt) are ISO-8601 in UTC. The time zone they were originally entered in is not stored.",
            "Every field is always present. A value that was not recorded is null, never left out; an empty collection is [].",
            "Text is exactly as it was stored, including any non-English characters.",
            "Ids are this account's own database ids. They exist to link records within this file (for example sets[].workoutExerciseId points at workoutExercises[].id) and every such reference resolves within the file.",
        },
        account = new
        {
            id = "Integer. The account's id; the userId in exercises and workouts refers to it.",
            privacyAccountId = "UUID. The account's permanent privacy identity, used to identify the account in deletion records. Not a password or sign-in credential.",
            username = "The name used to sign in.",
            createdAt = "UTC instant the account was created.",
        },
        exercises = new
        {
            order = "By id.",
            note = "Every exercise the account has defined, including ones no workout uses.",
            id = "Integer. Referenced by workoutExercises[].exerciseId.",
            userId = "The owning account's id.",
            name = "The exercise's name as entered.",
            isBodyweight = "Boolean. The exercise's current classification as a bodyweight exercise. It is not a record of how earlier workouts were classified.",
            createdAt = "UTC instant the exercise was created.",
        },
        workouts = new
        {
            order = "By id.",
            id = "Integer. Referenced by workoutExercises[].workoutId.",
            userId = "The owning account's id.",
            date = "YYYY-MM-DD. The local calendar day the workout belongs to, stored separately. It is not derived from startedAt and may differ from startedAt's UTC date.",
            startedAt = "UTC instant the workout started.",
            endedAt = "UTC instant the workout ended, or null if it was never finished. Null does not mean snapshotAt.",
            title = "Optional text, or null when none was recorded.",
            location = "Optional text, or null when none was recorded.",
            notes = "Optional text, or null when none was recorded.",
            bodyweightKg = "Decimal number in kilograms, as stored without rounding, or null when not recorded.",
            createdAt = "UTC instant the workout record was created.",
        },
        workoutExercises = new
        {
            order = "By workoutId, then position, then id.",
            note = "One block per exercise performed in a workout. The same exercise can appear in several blocks of one workout; each block is a separate record.",
            id = "Integer. Referenced by sets[].workoutExerciseId.",
            workoutId = "The workout this block belongs to (workouts[].id).",
            exerciseId = "The exercise performed (exercises[].id).",
            position = "Integer. The block's place within its workout; lower comes first. Stored values, not renumbered.",
        },
        sets = new
        {
            order = "By workoutExerciseId, then setNumber, then id.",
            id = "Integer. The set's id.",
            workoutExerciseId = "The block this set belongs to (workoutExercises[].id).",
            setNumber = "Integer. The set's place within its block; lower comes first. Stored values, not renumbered.",
            weight = "Decimal number in kilograms, as stored without rounding, or null when no weight was recorded. Null is not zero. On a bodyweight exercise this is the added load only.",
            reps = "Integer. The repetitions recorded.",
            isWarmup = "Boolean. Whether the set was marked as a warm-up.",
        },
        privacyRecords = new
        {
            noticeAcknowledgement = "Null, or { noticeVersion, acknowledgedAt }: the latest privacy notice version this account continued past, and the UTC instant it did. This records that the notice was shown; it is not consent. Null means no acknowledgement is held, not a refusal.",
            optionalDetailsConsent = "Null, or { statementVersion, consentedAt }: the account's current consent to recording workout title, location, notes and bodyweight. Null means no current consent; refusals and withdrawals are not recorded.",
        },
    };
}
