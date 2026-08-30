using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CallTree.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRecordingName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "Recording",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            // Backfill: reproduce RecordingName.Default for rows that predate the column, so that no
            // recording is left with the empty name the ADD COLUMN above gave it.
            //
            // This SQL is a one-time snapshot of that C# and is deliberately NOT kept in sync with it:
            // once it has run, these are values the operator may have edited, and a later change to the
            // default scheme has no business rewriting them.
            //
            // Timestamps are stored by UtcDateTimeOffsetConverter as UTC text in EF's own format
            // ("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz"), so the first 16 characters are always
            // "yyyy-MM-dd HH:mm" whether or not a fractional part was written.
            migrationBuilder.Sql(
                """
                UPDATE Recording
                SET Name =
                    CASE
                        WHEN (SELECT c.Source FROM Calls c WHERE c.Id = Recording.CallId) = 'Outbound'
                            THEN 'Outbound call'
                        WHEN (
                            SELECT CASE
                                WHEN l.RemoteNumber IS NOT NULL
                                     AND length(l.RemoteNumber) = 12
                                     AND substr(l.RemoteNumber, 1, 2) = '+1'
                                    THEN '(' || substr(l.RemoteNumber, 3, 3) || ') '
                                         || substr(l.RemoteNumber, 6, 3) || '-'
                                         || substr(l.RemoteNumber, 9, 4)
                                WHEN l.RemoteNumber IS NOT NULL THEN l.RemoteNumber
                                WHEN trim(l.RawCallerId) <> '' THEN substr(trim(l.RawCallerId), 1, 64)
                            END
                            FROM CallLeg l
                            WHERE l.CallId = Recording.CallId AND l.Direction = 'Inbound'
                            LIMIT 1) IS NULL
                            THEN 'Inbound call'
                        ELSE 'Inbound call from ' || (
                            SELECT CASE
                                WHEN l.RemoteNumber IS NOT NULL
                                     AND length(l.RemoteNumber) = 12
                                     AND substr(l.RemoteNumber, 1, 2) = '+1'
                                    THEN '(' || substr(l.RemoteNumber, 3, 3) || ') '
                                         || substr(l.RemoteNumber, 6, 3) || '-'
                                         || substr(l.RemoteNumber, 9, 4)
                                WHEN l.RemoteNumber IS NOT NULL THEN l.RemoteNumber
                                ELSE substr(trim(l.RawCallerId), 1, 64)
                            END
                            FROM CallLeg l
                            WHERE l.CallId = Recording.CallId AND l.Direction = 'Inbound'
                            LIMIT 1)
                    END
                    || ', '
                    || CASE substr(Recording.CreatedAt, 6, 2)
                        WHEN '01' THEN 'Jan' WHEN '02' THEN 'Feb' WHEN '03' THEN 'Mar'
                        WHEN '04' THEN 'Apr' WHEN '05' THEN 'May' WHEN '06' THEN 'Jun'
                        WHEN '07' THEN 'Jul' WHEN '08' THEN 'Aug' WHEN '09' THEN 'Sep'
                        WHEN '10' THEN 'Oct' WHEN '11' THEN 'Nov' ELSE 'Dec'
                       END
                    || ' ' || substr(Recording.CreatedAt, 9, 2)
                    || ' ' || substr(Recording.CreatedAt, 1, 4)
                    || ' ' || substr(Recording.CreatedAt, 12, 5)
                WHERE Name = '';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Name",
                table: "Recording");
        }
    }
}
