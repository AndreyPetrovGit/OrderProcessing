using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OrderProcessing.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryPriceAndSeedData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Price",
                table: "Inventory",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.InsertData(
                table: "Inventory",
                columns: new[] { "ProductId", "Price", "Quantity" },
                values: new object[,]
                {
                    { "PROD-001", 29.99m, 100 },
                    { "PROD-002", 49.99m, 50 },
                    { "PROD-003", 9.99m, 200 }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "Inventory",
                keyColumn: "ProductId",
                keyValue: "PROD-001");

            migrationBuilder.DeleteData(
                table: "Inventory",
                keyColumn: "ProductId",
                keyValue: "PROD-002");

            migrationBuilder.DeleteData(
                table: "Inventory",
                keyColumn: "ProductId",
                keyValue: "PROD-003");

            migrationBuilder.DropColumn(
                name: "Price",
                table: "Inventory");
        }
    }
}
