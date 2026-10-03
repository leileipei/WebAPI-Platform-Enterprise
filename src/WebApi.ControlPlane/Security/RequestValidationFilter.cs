using System.Collections;
using System.Reflection;
using WebApi.Contracts.Common;
namespace WebApi.ControlPlane.Security;
public sealed class RequestValidationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context,EndpointFilterDelegate next)
    {
        foreach(var argument in context.Arguments)
            if(argument is not null && IsContract(argument.GetType())) Validate(argument,0);
        return await next(context);
    }
    private static bool IsContract(Type type)=>type.Namespace?.StartsWith("WebApi.Contracts",StringComparison.Ordinal)==true||type.IsArray&&IsContract(type.GetElementType()!)||type.IsGenericType&&type.GetGenericArguments().Any(IsContract);
    private static void Validate(object? value,int depth)
    {
        if(value is null||depth>16) throw Invalid();
        if(value is IEnumerable enumerable && value is not string)
        {
            var count=0;foreach(var item in enumerable) {if(++count>5000) throw Invalid();Validate(item,depth+1);}return;
        }
        if(!IsContract(value.GetType())) return;
        var nullability=new NullabilityInfoContext();
        foreach(var property in value.GetType().GetProperties(BindingFlags.Public|BindingFlags.Instance))
        {
            var field=property.GetValue(value);
            if(field is null) {if(nullability.Create(property).ReadState==NullabilityState.NotNull) throw Invalid();continue;}
            if(field is Guid id&&id==Guid.Empty) throw Invalid();
            if(field is string text && text.Length>4*1024*1024) throw Invalid();
            if(IsContract(property.PropertyType)) Validate(field,depth+1);
        }
    }
    private static ApiException Invalid()=>new(422,"invalid_request","请求缺少必填字段、包含空子项或超过允许大小。");
}
