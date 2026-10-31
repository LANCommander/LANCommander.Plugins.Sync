using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LANCommander.Plugins.Sync.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Plugin_Sync_Records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RemoteId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordType = table.Column<int>(type: "integer", nullable: false),
                    RecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RemoteFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LocalFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OriginId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncedOn = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastAttemptOn = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Plugin_Sync_Records", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Plugin_Sync_Runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RemoteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Trigger = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StartedOn = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedOn = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Checked = table.Column<int>(type: "integer", nullable: false),
                    Changed = table.Column<int>(type: "integer", nullable: false),
                    Imported = table.Column<int>(type: "integer", nullable: false),
                    Failed = table.Column<int>(type: "integer", nullable: false),
                    Downloaded = table.Column<long>(type: "bigint", nullable: false),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Plugin_Sync_Runs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Plugin_Sync_Records_RecordType_RecordId",
                table: "Plugin_Sync_Records",
                columns: new[] { "RecordType", "RecordId" });

            migrationBuilder.CreateIndex(
                name: "IX_Plugin_Sync_Records_RemoteId_RecordType_RecordId",
                table: "Plugin_Sync_Records",
                columns: new[] { "RemoteId", "RecordType", "RecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Plugin_Sync_Runs_RemoteId_StartedOn",
                table: "Plugin_Sync_Runs",
                columns: new[] { "RemoteId", "StartedOn" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Plugin_Sync_Records");

            migrationBuilder.DropTable(
                name: "Plugin_Sync_Runs");
        }
    }
}
