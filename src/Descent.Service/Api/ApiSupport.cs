using Google.Protobuf;
using Grpc.Core;
using Grpc.Core.Interceptors;
using SourceSharp.Host.Abstractions;

namespace Descent.Service.Api;

/// <summary>
/// The game API's auth (plan §6): x-instance-id + x-instance-token against the instance's
/// token hash; anything else UNAUTHENTICATED. The authenticated instance row travels in the
/// call's UserState for the handlers.
/// </summary>
public sealed class InstanceAuthInterceptor(IInstanceLifecycle lifecycle) : Interceptor
{
    public const string InstanceKey = "descent.instance";

    async Task Authenticate(ServerCallContext ctx)
    {
        var id = ctx.RequestHeaders.GetValue("x-instance-id");
        var token = ctx.RequestHeaders.GetValue("x-instance-token");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(token))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "bad_token: missing x-instance-id / x-instance-token"));
        var instance = await lifecycle.Authenticate(id, token, ctx.CancellationToken)
                       ?? throw new RpcException(new Status(StatusCode.Unauthenticated, "bad_token: unknown instance or token"));
        ctx.UserState[InstanceKey] = instance;
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request, ServerCallContext ctx, UnaryServerMethod<TRequest, TResponse> next)
    { await Authenticate(ctx); return await next(request, ctx); }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> stream, ServerCallContext ctx, ClientStreamingServerMethod<TRequest, TResponse> next)
    { await Authenticate(ctx); return await next(stream, ctx); }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(TRequest request, IServerStreamWriter<TResponse> stream, ServerCallContext ctx, ServerStreamingServerMethod<TRequest, TResponse> next)
    { await Authenticate(ctx); await next(request, stream, ctx); }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> input, IServerStreamWriter<TResponse> output, ServerCallContext ctx, DuplexStreamingServerMethod<TRequest, TResponse> next)
    { await Authenticate(ctx); await next(input, output, ctx); }
}

public static class Api
{
    public static InstanceRecord Caller(this ServerCallContext ctx) =>
        ctx.UserState.TryGetValue(InstanceAuthInterceptor.InstanceKey, out var v) && v is InstanceRecord i
            ? i : throw new RpcException(new Status(StatusCode.Unauthenticated, "bad_token: not authenticated"));

    /// <summary>A business refusal as a gRPC status "reason: text", mirrored in the x-reason trailer (proto/common.proto).</summary>
    public static RpcException ToRpc(HostRefusal e)
    {
        var code = e.Code switch
        {
            RefusalCode.FailedPrecondition => StatusCode.FailedPrecondition,
            RefusalCode.NotFound => StatusCode.NotFound,
            RefusalCode.PermissionDenied => StatusCode.PermissionDenied,
            RefusalCode.ResourceExhausted => StatusCode.ResourceExhausted,
            RefusalCode.Unauthenticated => StatusCode.Unauthenticated,
            RefusalCode.Unimplemented => StatusCode.Unimplemented,
            _ => StatusCode.InvalidArgument,
        };
        return new RpcException(new Status(code, e.Message), new Metadata { { "x-reason", e.Reason } });
    }

    /// <summary>
    /// Runs a mutating call once per request id (§4.4): a replay returns the stored response.
    /// Ids are namespaced by the calling instance, so two pods can never collide.
    /// </summary>
    public static async Task<T> Idempotent<T>(this IHostData data, ServerCallContext ctx, string requestId, MessageParser<T> parser,
        Func<IWriteTx, InstanceRecord, Task<T>> work) where T : IMessage<T>
    {
        var caller = ctx.Caller();
        if (string.IsNullOrEmpty(requestId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "missing_request_id: every mutating request carries request_id"));
        try
        {
            var r = await data.IdempotentAsync($"{caller.Id}:{requestId}", caller.Id,
                async (tx, _) => (await work(tx, caller)).ToByteArray(), ctx.CancellationToken);
            return parser.ParseFrom(r.Response);
        }
        catch (HostRefusal e) { throw ToRpc(e); }
    }

    /// <summary>Idempotency around an orchestration that runs several transactions itself (travel).</summary>
    public static async Task<T> IdempotentOutside<T>(this IHostData data, ServerCallContext ctx, string requestId, MessageParser<T> parser,
        Func<InstanceRecord, Task<T>> work) where T : IMessage<T>
    {
        var caller = ctx.Caller();
        if (string.IsNullOrEmpty(requestId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "missing_request_id: every mutating request carries request_id"));
        var key = $"{caller.Id}:{requestId}";
        if (await data.IdempotentLookupAsync(key, ctx.CancellationToken) is { } stored) return parser.ParseFrom(stored);
        try
        {
            var response = await work(caller);
            await data.IdempotentStoreAsync(key, caller.Id, response.ToByteArray(), ctx.CancellationToken);
            return response;
        }
        catch (HostRefusal e) { throw ToRpc(e); }
    }

    public static async Task<T> Read<T>(this IHostData data, ServerCallContext ctx, Func<IReadTx, InstanceRecord, Task<T>> work)
    {
        var caller = ctx.Caller();
        try { return await data.ReadAsync((tx, _) => work(tx, caller), ctx.CancellationToken); }
        catch (HostRefusal e) { throw ToRpc(e); }
    }

    public static async Task<T> Write<T>(this IHostData data, ServerCallContext ctx, Func<IWriteTx, InstanceRecord, Task<T>> work)
    {
        var caller = ctx.Caller();
        try { return await data.WriteAsync((tx, _) => work(tx, caller), ctx.CancellationToken); }
        catch (HostRefusal e) { throw ToRpc(e); }
    }

    public static byte[] Bytes(this ByteString b) => b.ToByteArray();
}
