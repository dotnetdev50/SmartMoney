using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartMoney.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Init_Create : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "market_bias",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Date = table.Column<DateTime>(type: "date", nullable: false),
                    RawBias = table.Column<double>(type: "float", nullable: false),
                    FinalScore = table.Column<double>(type: "float", nullable: false),
                    ShockScore = table.Column<double>(type: "float", nullable: false),
                    Regime = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_market_bias", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "participant_metrics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Date = table.Column<DateTime>(type: "date", nullable: false),
                    Participant = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    FuturesZShort = table.Column<double>(type: "float", nullable: false),
                    FuturesZLong = table.Column<double>(type: "float", nullable: false),
                    PutZShort = table.Column<double>(type: "float", nullable: false),
                    PutZLong = table.Column<double>(type: "float", nullable: false),
                    CallZShort = table.Column<double>(type: "float", nullable: false),
                    CallZLong = table.Column<double>(type: "float", nullable: false),
                    ParticipantBias = table.Column<double>(type: "float", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_participant_metrics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "participant_raw_data",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Date = table.Column<DateTime>(type: "date", nullable: false),
                    Participant = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    FuturesNet = table.Column<double>(type: "float", nullable: false),
                    FuturesChange = table.Column<double>(type: "float", nullable: false),
                    PutOiChange = table.Column<double>(type: "float", nullable: false),
                    CallOiChange = table.Column<double>(type: "float", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_participant_raw_data", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_market_bias_Date",
                table: "market_bias",
                column: "Date",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_participant_metrics_Date_Participant",
                table: "participant_metrics",
                columns: new[] { "Date", "Participant" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_participant_raw_data_Date_Participant",
                table: "participant_raw_data",
                columns: new[] { "Date", "Participant" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "market_bias");

            migrationBuilder.DropTable(
                name: "participant_metrics");

            migrationBuilder.DropTable(
                name: "participant_raw_data");
        }
    }
}
