using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrdersTenantCreatedIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Orders_TenantId_CreatedAt_Id",
                table: "Orders",
                columns: new[] { "TenantId", "CreatedAt", "Id" },
                descending: new[] { false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orders_TenantId_CreatedAt_Id",
                table: "Orders");
        }
    }
}
