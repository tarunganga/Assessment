using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ripple.Treasury.Assessment.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DropTicketSeatOrdinal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_tickets_available",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "uq_tickets_tier_ordinal",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "seat_ordinal",
                table: "tickets");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_available",
                table: "tickets",
                columns: new[] { "pricing_tier_id", "id" },
                filter: "status = 'Available'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_tickets_available",
                table: "tickets");

            migrationBuilder.AddColumn<int>(
                name: "seat_ordinal",
                table: "tickets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "ix_tickets_available",
                table: "tickets",
                columns: new[] { "pricing_tier_id", "seat_ordinal" },
                filter: "status = 'Available'");

            migrationBuilder.CreateIndex(
                name: "uq_tickets_tier_ordinal",
                table: "tickets",
                columns: new[] { "pricing_tier_id", "seat_ordinal" },
                unique: true);
        }
    }
}
