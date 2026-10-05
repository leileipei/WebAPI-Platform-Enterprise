using Microsoft.EntityFrameworkCore.Migrations;
namespace WebApi.Infrastructure.Persistence.Migrations;
public partial class SystemSettings:Migration
{
    protected override void Up(MigrationBuilder b)
    {
        b.CreateTable(name:"system_settings",columns:t=>new {key=t.Column<string>(type:"varchar(128)",nullable:false),scope_type=t.Column<string>(type:"varchar(24)",nullable:false),scope_id=t.Column<Guid>(type:"uuid",nullable:true),value=t.Column<string>(type:"jsonb",nullable:false),updated_by=t.Column<Guid>(type:"uuid",nullable:false),updated_at=t.Column<DateTimeOffset>(type:"timestamptz",nullable:false),revision=t.Column<long>(type:"bigint",nullable:false,defaultValue:1L)},constraints:t=>{t.PrimaryKey("PK_system_settings",x=>x.key);t.CheckConstraint("ck_system_setting_scope","scope_type = 'system' AND scope_id IS NULL");t.CheckConstraint("ck_system_setting_key","key IN ('system.security','system.release','system.gateway','system.audit','system.notification')");t.CheckConstraint("ck_system_setting_revision","revision > 0");t.ForeignKey("FK_system_settings_users_updated_by",x=>x.updated_by,"users","id",onDelete:ReferentialAction.Restrict);});b.CreateIndex("IX_system_settings_updated_by","system_settings","updated_by");
    }
    protected override void Down(MigrationBuilder b)=>b.DropTable("system_settings");
}
