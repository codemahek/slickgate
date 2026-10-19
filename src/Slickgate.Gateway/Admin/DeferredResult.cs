namespace Slickgate.Gateway.Admin;

/// <summary>
/// Awaits a <c>Task&lt;IResult&gt;</c> during result execution instead of returning the task
/// from the endpoint handler. Under Native AOT the Request Delegate Generator does not emit the
/// awaited delegate path for <c>Task&lt;IResult&gt;</c>-returning handlers; the framework boxes
/// the task and JSON-serializes it (500, "JsonTypeInfo metadata for type Task`1[IResult] ...").
/// Endpoint handlers therefore stay synchronous and wrap their async work here.
/// Upstream: <c>dotnet/aspnetcore#58509</c>. Remove once that is fixed.
/// </summary>
internal sealed class DeferredResult(Task<IResult> inner) : IResult
{
    public async Task ExecuteAsync(HttpContext context)
    {
        var result = await inner;
        await result.ExecuteAsync(context);
    }
}
