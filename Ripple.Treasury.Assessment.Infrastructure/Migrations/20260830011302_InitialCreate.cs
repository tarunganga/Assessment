using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ripple.Treasury.Assessment.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    venue = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    starts_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    total_capacity = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "Draft"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_events", x => x.id);
                    table.CheckConstraint("ck_events_capacity_positive", "total_capacity > 0");
                    table.CheckConstraint("ck_events_name_not_blank", "length(btrim(name)) > 0");
                    table.CheckConstraint("ck_events_status", "status IN ('Draft', 'Published', 'Cancelled')");
                    table.CheckConstraint("ck_events_venue_not_blank", "length(btrim(venue)) > 0");
                });

            migrationBuilder.CreateTable(
                name: "pricing_tiers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    price_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    price_currency = table.Column<string>(type: "char(3)", nullable: false),
                    allocation = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pricing_tiers", x => x.id);
                    table.CheckConstraint("ck_pricing_tiers_allocation_positive", "allocation > 0");
                    table.CheckConstraint("ck_pricing_tiers_currency_iso", "price_currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_pricing_tiers_name_not_blank", "length(btrim(name)) > 0");
                    table.CheckConstraint("ck_pricing_tiers_price_non_negative", "price_amount >= 0");
                    table.ForeignKey(
                        name: "fk_pricing_tiers_events_event_id",
                        column: x => x.event_id,
                        principalTable: "events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "purchases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    request_fingerprint = table.Column<string>(type: "char(64)", nullable: false),
                    purchaser_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "Completed"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_purchases", x => x.id);
                    table.CheckConstraint("ck_purchases_currency_iso", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_purchases_email_shape", "purchaser_email ~ '^[^@[:space:]]+@[^@[:space:]]+\\.[^@[:space:]]+$'");
                    table.CheckConstraint("ck_purchases_status", "status IN ('Completed', 'Cancelled')");
                    table.CheckConstraint("ck_purchases_total_non_negative", "total_amount >= 0");
                    table.ForeignKey(
                        name: "fk_purchases_events_event_id",
                        column: x => x.event_id,
                        principalTable: "events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "purchase_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    purchase_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pricing_tier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    item_total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_purchase_items", x => x.id);
                    table.CheckConstraint("ck_purchase_items_price_non_negative", "unit_price >= 0");
                    table.CheckConstraint("ck_purchase_items_quantity_positive", "quantity > 0");
                    table.CheckConstraint("ck_purchase_items_total_consistent", "item_total = unit_price * quantity");
                    table.ForeignKey(
                        name: "fk_purchase_items_pricing_tiers_pricing_tier_id",
                        column: x => x.pricing_tier_id,
                        principalTable: "pricing_tiers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_purchase_items_purchases_purchase_id",
                        column: x => x.purchase_id,
                        principalTable: "purchases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tickets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pricing_tier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seat_ordinal = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "Available"),
                    purchase_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sold_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tickets", x => x.id);
                    table.CheckConstraint("ck_tickets_sold_has_purchase", "(status = 'Available' AND purchase_id IS NULL     AND sold_at IS NULL)\nOR (status = 'Sold'      AND purchase_id IS NOT NULL AND sold_at IS NOT NULL)");
                    table.CheckConstraint("ck_tickets_status", "status IN ('Available', 'Sold')");
                    table.ForeignKey(
                        name: "fk_tickets_events_event_id",
                        column: x => x.event_id,
                        principalTable: "events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_tickets_pricing_tiers_pricing_tier_id",
                        column: x => x.pricing_tier_id,
                        principalTable: "pricing_tiers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_tickets_purchases_purchase_id",
                        column: x => x.purchase_id,
                        principalTable: "purchases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_events_starts_at",
                table: "events",
                column: "starts_at_utc",
                filter: "status = 'Published'");

            migrationBuilder.CreateIndex(
                name: "ix_pricing_tiers_event",
                table: "pricing_tiers",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "uq_pricing_tiers_event_name",
                table: "pricing_tiers",
                columns: new[] { "event_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_purchase_items_purchase",
                table: "purchase_items",
                column: "purchase_id");

            migrationBuilder.CreateIndex(
                name: "ix_purchase_items_tier",
                table: "purchase_items",
                column: "pricing_tier_id");

            migrationBuilder.CreateIndex(
                name: "uq_purchase_items_purchase_tier",
                table: "purchase_items",
                columns: new[] { "purchase_id", "pricing_tier_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_purchases_created",
                table: "purchases",
                column: "created_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_purchases_event",
                table: "purchases",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "uq_purchases_idempotency_key",
                table: "purchases",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tickets_available",
                table: "tickets",
                columns: new[] { "pricing_tier_id", "seat_ordinal" },
                filter: "status = 'Available'");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_event_tier",
                table: "tickets",
                columns: new[] { "event_id", "pricing_tier_id" });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_purchase",
                table: "tickets",
                column: "purchase_id",
                filter: "purchase_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "uq_tickets_tier_ordinal",
                table: "tickets",
                columns: new[] { "pricing_tier_id", "seat_ordinal" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "purchase_items");

            migrationBuilder.DropTable(
                name: "tickets");

            migrationBuilder.DropTable(
                name: "pricing_tiers");

            migrationBuilder.DropTable(
                name: "purchases");

            migrationBuilder.DropTable(
                name: "events");
        }
    }
}
