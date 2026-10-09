using System.Collections;
using System.Data;
using System.Data.Common;
namespace WebApi.PerformanceHost;
// Counts consumed rows, not SQL text, parameters, values, or estimated scan rows.
internal sealed class PerformanceCountingReader(DbDataReader inner,Action onRow,Action<long>? onBinaryBytes=null):DbDataReader
{
    private HashSet<int>? binaryRead;
    private T Observe<T>(int ordinal,T value){if(value is byte[] bytes&&onBinaryBytes is not null&&(binaryRead??=new()).Add(ordinal))onBinaryBytes(bytes.LongLength);return value;}
    public override bool Read(){var found=inner.Read();binaryRead?.Clear();if(found)onRow();return found;}
    public override async Task<bool> ReadAsync(CancellationToken cancellationToken){var found=await inner.ReadAsync(cancellationToken);binaryRead?.Clear();if(found)onRow();return found;}
    public override bool NextResult()=>inner.NextResult();
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken)=>inner.NextResultAsync(cancellationToken);
    public override object this[int ordinal]=>Observe(ordinal,inner[ordinal]);
    public override object this[string name]=>Observe(inner.GetOrdinal(name),inner[name]);
    public override int Depth=>inner.Depth;
    public override int FieldCount=>inner.FieldCount;
    public override int VisibleFieldCount=>inner.VisibleFieldCount;
    public override bool HasRows=>inner.HasRows;
    public override bool IsClosed=>inner.IsClosed;
    public override int RecordsAffected=>inner.RecordsAffected;
    public override void Close()=>inner.Close();
    public override Task CloseAsync()=>inner.CloseAsync();
    protected override void Dispose(bool disposing){if(disposing)inner.Dispose();}
    public override ValueTask DisposeAsync()=>inner.DisposeAsync();
    public override bool GetBoolean(int ordinal)=>inner.GetBoolean(ordinal);
    public override byte GetByte(int ordinal)=>inner.GetByte(ordinal);
    public override long GetBytes(int ordinal,long dataOffset,byte[]? buffer,int bufferOffset,int length)=>inner.GetBytes(ordinal,dataOffset,buffer,bufferOffset,length);
    public override char GetChar(int ordinal)=>inner.GetChar(ordinal);
    public override long GetChars(int ordinal,long dataOffset,char[]? buffer,int bufferOffset,int length)=>inner.GetChars(ordinal,dataOffset,buffer,bufferOffset,length);
    public override string GetDataTypeName(int ordinal)=>inner.GetDataTypeName(ordinal);
    public override DateTime GetDateTime(int ordinal)=>inner.GetDateTime(ordinal);
    public override decimal GetDecimal(int ordinal)=>inner.GetDecimal(ordinal);
    public override double GetDouble(int ordinal)=>inner.GetDouble(ordinal);
    public override Type GetFieldType(int ordinal)=>inner.GetFieldType(ordinal);
    public override T GetFieldValue<T>(int ordinal)=>Observe(ordinal,inner.GetFieldValue<T>(ordinal));
    public override async Task<T> GetFieldValueAsync<T>(int ordinal,CancellationToken cancellationToken)=>Observe(ordinal,await inner.GetFieldValueAsync<T>(ordinal,cancellationToken));
    public override float GetFloat(int ordinal)=>inner.GetFloat(ordinal);
    public override Guid GetGuid(int ordinal)=>inner.GetGuid(ordinal);
    public override short GetInt16(int ordinal)=>inner.GetInt16(ordinal);
    public override int GetInt32(int ordinal)=>inner.GetInt32(ordinal);
    public override long GetInt64(int ordinal)=>inner.GetInt64(ordinal);
    public override string GetName(int ordinal)=>inner.GetName(ordinal);
    public override int GetOrdinal(string name)=>inner.GetOrdinal(name);
    public override string GetString(int ordinal)=>inner.GetString(ordinal);
    public override object GetValue(int ordinal)=>Observe(ordinal,inner.GetValue(ordinal));
    public override int GetValues(object[] values){var count=inner.GetValues(values);for(var i=0;i<count;i++)Observe(i,values[i]);return count;}
    public override bool IsDBNull(int ordinal)=>inner.IsDBNull(ordinal);
    public override Task<bool> IsDBNullAsync(int ordinal,CancellationToken cancellationToken)=>inner.IsDBNullAsync(ordinal,cancellationToken);
    public override DataTable? GetSchemaTable()=>inner.GetSchemaTable();
    public override Stream GetStream(int ordinal)=>inner.GetStream(ordinal);
    public override TextReader GetTextReader(int ordinal)=>inner.GetTextReader(ordinal);
    public override IEnumerator GetEnumerator()=>new DbEnumerator(this,false);
}
