namespace WebApi.Domain.Runtime;
public static class SnapshotSchemaRules
{
    private static readonly string[] Schemas=["2.0","2.1","2.2","2.3","2.4"];
    public static bool AtLeast(string schema,string minimum)=>Array.IndexOf(Schemas,schema) is var actual && actual>=0 && Array.IndexOf(Schemas,minimum) is var floor && floor>=0 && actual>=floor;
}
