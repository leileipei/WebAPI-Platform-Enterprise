using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace WebApi.Infrastructure.Persistence.Migrations;
[DbContext(typeof(WebApiDbContext))]
[Migration("20261010010000_PipelineProjectionCursor")]
public sealed class PipelineProjectionCursor:Migration
{
 protected override void Up(MigrationBuilder migrationBuilder)
 {migrationBuilder.AddColumn<DateTimeOffset>(name:"projection_checked_at",table:"release_pipeline_runs",type:"timestamptz",nullable:true);migrationBuilder.CreateIndex(name:"ix_pipeline_projection_due",table:"release_pipeline_runs",columns:["status","projection_checked_at","id"]);}
 protected override void Down(MigrationBuilder migrationBuilder)
 {migrationBuilder.DropIndex(name:"ix_pipeline_projection_due",table:"release_pipeline_runs");migrationBuilder.DropColumn(name:"projection_checked_at",table:"release_pipeline_runs");}
}
