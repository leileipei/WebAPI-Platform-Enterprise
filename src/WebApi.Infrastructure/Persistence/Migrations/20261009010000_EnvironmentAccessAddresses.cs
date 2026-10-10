using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnvironmentAccessAddresses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "access_address_revision",
                table: "environments",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<string>(
                name: "base_path",
                table: "environments",
                type: "varchar(512)",
                nullable: false,
                defaultValue: "/");

            migrationBuilder.AddColumn<string>(
                name: "gateway_internal_url",
                table: "environments",
                type: "varchar(2048)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "gateway_public_url",
                table: "environments",
                type: "varchar(2048)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "access_address_revision",
                table: "environments");

            migrationBuilder.DropColumn(
                name: "base_path",
                table: "environments");

            migrationBuilder.DropColumn(
                name: "gateway_internal_url",
                table: "environments");

            migrationBuilder.DropColumn(
                name: "gateway_public_url",
                table: "environments");
        }
    }
}
