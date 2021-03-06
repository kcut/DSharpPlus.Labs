using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Voltaic;
using Voltaic.Serialization;
using Wumpus.Events;
using Wumpus.Requests;
using Wumpus.Serialization;

namespace Wumpus
{
    public enum ConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Disconnecting
    }

    public class WumpusRpcClient : IDisposable
    {
        public const int ApiVersion = 1;
        public static string Version { get; } =
            typeof(WumpusRpcClient).GetTypeInfo().Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
            typeof(WumpusRpcClient).GetTypeInfo().Assembly.GetName().Version.ToString(3) ??
            "Unknown";

        public const int PortRangeStart = 6463;
        public const int PortRangeEnd = 6472;

        public const int InitialBackoffMillis = 1000; // 1 second
        public const int MaxBackoffMillis = 60000; // 1 min
        public const double BackoffMultiplier = 1.75; // 1.75x
        public const double BackoffJitter = 0.25; // 1.5x to 2.0x
        public const int ConnectionTimeoutMillis = 30000; // 30 secs
        public const int AuthenticateTimeoutMillis = 60000; // 1 min
        // Typical Backoff: 1.75s, 3.06s, 5.36s, 9.38s, 16.41s, 28.72s, 50.27s, 60s, 60s...
        
        // Status events
        public event Action Connected;
        public event Action<Exception> Disconnected;
        public event Action<SerializationException> DeserializationError;

        // Raw events
        public event Action<RpcPayload, PayloadInfo> ReceivedPayload;
        public event Action<RpcPayload, PayloadInfo> SentPayload;

        // Rpc events //TODO: Impl

        // Instance
        private readonly ResizableMemoryStream _decompressed;
        private readonly SemaphoreSlim _stateLock;
        private Task _connectionTask;
        private CancellationTokenSource _runCts;

        // Run (Start/Stop)
        private string _url;

        // Connection (For each WebSocket connection)
        private BlockingCollection<RpcPayload> _sendQueue;

        public Snowflake ClientId { get; set; }
        public string Origin { get; set; }
        public string[] Scopes { get; set; }
        public AuthenticationHeaderValue Authorization { get; set; }
        public ConnectionState State { get; private set; }
        public WumpusJsonSerializer JsonSerializer { get; }

        public WumpusRpcClient(WumpusJsonSerializer serializer = null)
        {
            JsonSerializer = serializer ?? new WumpusJsonSerializer();
            _decompressed = new ResizableMemoryStream(10 * 1024); // 10 KB
            _stateLock = new SemaphoreSlim(1, 1);
            _connectionTask = Task.CompletedTask;
            _runCts = new CancellationTokenSource();
            _runCts.Cancel(); // Start canceled
        }

        public void Run()
            => RunAsync().GetAwaiter().GetResult();
        public async Task RunAsync()
        {
            Task exceptionSignal;
            await _stateLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopAsyncInternal().ConfigureAwait(false);

                _url = null;
                _runCts = new CancellationTokenSource();
                
                _connectionTask = RunTaskAsync(_runCts.Token);
                exceptionSignal = _connectionTask;
            }
            finally
            {
                _stateLock.Release();
            }
            await exceptionSignal.ConfigureAwait(false);
        }
        private async Task RunTaskAsync(CancellationToken runCancelToken)
        {
            Task[] tasks = null;
            bool isRecoverable = true;
            int backoffMillis = InitialBackoffMillis;
            var jitter = new Random();

            while (isRecoverable)
            {
                Exception disconnectEx = null;
                var connectionCts = new CancellationTokenSource();
                var cancelToken = CancellationTokenSource.CreateLinkedTokenSource(runCancelToken, connectionCts.Token).Token;
                using (var client = new ClientWebSocket())
                {
                    client.Options.SetRequestHeader("origin", Origin);
                    try
                    {
                        cancelToken.ThrowIfCancellationRequested();
                        var readySignal = new TaskCompletionSource<bool>();

                        // Connect
                        State = ConnectionState.Connecting;

                        if (_url == null)
                            await SearchForServerAsync(client, cancelToken).ConfigureAwait(false);
                        else
                        {
                            // Reconnect to previously found server
                            string fullUrl = _url + $"?v={ApiVersion}&client_id={ClientId}&encoding=json";
                            var uri = new Uri(fullUrl);
                            await client.ConnectAsync(uri, cancelToken).ConfigureAwait(false);                                        
                        }

                        {
                            var receiveTask = ReceiveAsync(client, readySignal, cancelToken);
                            await WhenAny(new Task[] { receiveTask }, ConnectionTimeoutMillis, 
                                "Timed out waiting for READY").ConfigureAwait(false);
                            var evnt = await receiveTask.ConfigureAwait(false);
                            if (!(evnt.Data is ReadyEvent readyEvent))
                                throw new Exception("First event was not a READY cmd");
                        }
                        {
                            await SendAsync(client, cancelToken, new RpcPayload
                            {
                                Command = RpcCommand.Authenticate,
                                Args = new AuthenticateParams
                                {
                                    AccessToken = new Utf8String(Authorization.Parameter)
                                }
                            }).ConfigureAwait(false);
                            var receiveTask = ReceiveAsync(client, readySignal, cancelToken);
                            await WhenAny(new Task[] { receiveTask }, AuthenticateTimeoutMillis, 
                                "Timed out waiting for AUTHENTICATE").ConfigureAwait(false);
                            var evnt = await receiveTask.ConfigureAwait(false);
                            if (!(evnt.Data is AuthenticateResponse authenticateEvent))
                                throw new Exception("Authenticate response was not a AUTHENTICATE cmd");
                        }

                        // Start tasks here since HELLO must be handled before another thread can send/receive
                        _sendQueue = new BlockingCollection<RpcPayload>();
                        tasks = new[]
                        {
                            RunSendAsync(client, cancelToken),
                            RunReceiveAsync(client, readySignal, cancelToken)
                        };

                        // Success
                        backoffMillis = InitialBackoffMillis;
                        State = ConnectionState.Connected;
                        Connected?.Invoke();

                        // Wait until an exception occurs (due to cancellation or failure)
                        var task = await Task.WhenAny(tasks).ConfigureAwait(false);
                        if (task.IsFaulted)
                            await task.ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        disconnectEx = ex;
                        isRecoverable = IsRecoverable(ex);
                        if (!isRecoverable)
                            throw;
                    }
                    finally
                    {
                        var oldState = State;
                        State = ConnectionState.Disconnecting;

                        // Wait for the other tasks to complete
                        connectionCts.Cancel();
                        if (tasks != null)
                        {
                            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
                            catch { } // We already captured the root exception
                        }

                        // receiveTask and sendTask must have completed before we can send/receive from a different thread
                        if (client.State == WebSocketState.Open)
                        {
                            try { await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(false); }
                            catch { } // We don't actually care if sending a close msg fails
                        }

                        State = ConnectionState.Disconnected;
                        if (oldState == ConnectionState.Connected)
                            Disconnected?.Invoke(disconnectEx);

                        if (isRecoverable)
                        {
                            backoffMillis = (int)(backoffMillis * (BackoffMultiplier + (jitter.NextDouble() * BackoffJitter * 2.0 - BackoffJitter)));
                            if (backoffMillis > MaxBackoffMillis)
                                backoffMillis = MaxBackoffMillis;
                            await Task.Delay(backoffMillis).ConfigureAwait(false);
                        }
                    }
                }
            }
            _runCts.Cancel(); // Reset to initial canceled state
        }
        private Task RunReceiveAsync(ClientWebSocket client, TaskCompletionSource<bool> readySignal, CancellationToken cancelToken)
        {
            return Task.Run(async () =>
            {
                while (true)
                {
                    cancelToken.ThrowIfCancellationRequested();
                    try
                    {
                        await ReceiveAsync(client, readySignal, cancelToken).ConfigureAwait(false);
                    }
                    catch (WumpusRpcException ex) when (ex.Code == 4005) // Unknown id
                    {
                        // Ignore for now - this should be sent through to the promise that cause the error
                    }
                    catch (SerializationException ex)
                    {
                        DeserializationError?.Invoke(ex);
                    }
                }
            });
        }
        private Task RunSendAsync(ClientWebSocket client, CancellationToken cancelToken)
        {
            return Task.Run(async () =>
            {
                while (true)
                {
                    cancelToken.ThrowIfCancellationRequested();
                    var payload = _sendQueue.Take(cancelToken);
                    await SendAsync(client, cancelToken, payload).ConfigureAwait(false);
                }
            });
        }

        public async Task<Utf8String> AuthorizeAsync(CancellationToken cancelToken)
        {
            using (var client = new ClientWebSocket())
            {
                client.Options.SetRequestHeader("origin", Origin);
                await SearchForServerAsync(client, cancelToken).ConfigureAwait(false);
                
                {
                    var receiveTask = ReceiveAsync(client, null, cancelToken);
                    await WhenAny(new Task[] { receiveTask }, ConnectionTimeoutMillis, 
                        "Timed out waiting for READY").ConfigureAwait(false);
                    var evnt = await receiveTask.ConfigureAwait(false);
                    if (!(evnt.Data is ReadyEvent readyEvent))
                        throw new Exception("First event was not a READY cmd");
                }

                await SendAsync(client, cancelToken, new RpcPayload
                {
                    Command = RpcCommand.Authorize,
                    Args = new AuthorizeParams
                    {
                        ClientId = new Utf8String(ClientId.ToString()),
                        Scopes = Scopes?.Select(x => new Utf8String(x)).ToArray() ?? Array.Empty<Utf8String>()
                    }
                }).ConfigureAwait(false);
                {
                    var receiveTask = ReceiveAsync(client, null, cancelToken);
                    await WhenAny(new Task[] { receiveTask, Task.Delay(-1, cancelToken) }).ConfigureAwait(false);
                    var evnt = await receiveTask.ConfigureAwait(false);
                    if (!(evnt.Data is AuthorizeResponse authorizeEvent))
                        throw new Exception("Authorize response was not a AUTHORIZE cmd");
                    return authorizeEvent.Code;
                }
            }
        }

        public async Task SearchForServerAsync(ClientWebSocket client, CancellationToken cancelToken)
        {
            for (int port = PortRangeStart; port <= PortRangeEnd; port++)
            {
                try
                {
                    string url = $"ws://127.0.0.1:{port}";
                    string fullUrl = url + $"?v={ApiVersion}&client_id={ClientId}&encoding=json";

                    var uri = new Uri(fullUrl);
                    await client.ConnectAsync(uri, cancelToken).ConfigureAwait(false);
                    _url = url;
                    break;
                }
                catch (Exception) { }
            }
            if (_url == null)
                throw new WebSocketClosedException(WebSocketCloseStatus.EndpointUnavailable, "Failed to locate local RPC server"); // TODO: Make a custom exception?
        }

        private async Task WhenAny(IEnumerable<Task> tasks)
        {
            var task = await Task.WhenAny(tasks).ConfigureAwait(false);
            //if (task.IsFaulted)
            await task.ConfigureAwait(false); // Return or rethrow
        }
        private async Task WhenAny(IEnumerable<Task> tasks, int millis, string errorText)
        {
            var timeoutTask = Task.Delay(millis);
            var task = await Task.WhenAny(tasks.Append(timeoutTask)).ConfigureAwait(false);
            if (task == timeoutTask)
                throw new TimeoutException(errorText);
            //else if (task.IsFaulted)
            await task.ConfigureAwait(false); // Return or rethrow
        }

        private bool IsRecoverable(Exception ex)
        {
            switch (ex)
            {
                case HttpRequestException _:
                    _url = null;
                    return true;
                case WebSocketException wsEx:
                    if (wsEx.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
                        return true;
                    break;
                case WebSocketClosedException wscEx:
                    if (wscEx.CloseStatus.HasValue)
                    {
                        switch (wscEx.CloseStatus.Value)
                        {
                            case WebSocketCloseStatus.Empty:
                            case WebSocketCloseStatus.NormalClosure:
                            case WebSocketCloseStatus.InternalServerError:
                            case WebSocketCloseStatus.ProtocolError:
                                return true;
                            case WebSocketCloseStatus.EndpointUnavailable:
                                _url = null;
                                return true;
                        }
                    }
                    else
                    {
                        // https://discordapp.com/developers/docs/topics/opcodes-and-status-codes#rpc-rpc-close-event-codes
                        switch (wscEx.Code)
                        {
                            case 4002: // Rate Limited // TODO: Handle this better
                                return true;
                        }
                    }
                    break;
            }
            if (ex.InnerException != null)
                return IsRecoverable(ex.InnerException);
            return false;
        }

        public void Stop()
            => StopAsync().GetAwaiter().GetResult();
        public async Task StopAsync()
        {
            await _stateLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopAsyncInternal().ConfigureAwait(false);
            }
            finally
            {
                _stateLock.Release();
            }
        }
        private async Task StopAsyncInternal()
        {
            _runCts?.Cancel(); // Cancel any connection attempts or active connections

            try { await _connectionTask.ConfigureAwait(false); } catch { } // Wait for current connection to complete
            _connectionTask = Task.CompletedTask;

            // Double check that the connection task terminated successfully
            var state = State;
            if (state != ConnectionState.Disconnected)
                throw new InvalidOperationException($"Client did not successfully disconnect (State = {state}).");
        }

        public void Dispose()
        {
            Stop();
        }

        private async Task<RpcPayload> ReceiveAsync(ClientWebSocket client, TaskCompletionSource<bool> readySignal, CancellationToken cancelToken)
        {
            // Reset stream
            _decompressed.Position = 0;
            _decompressed.SetLength(0);

            // Receive data
            WebSocketReceiveResult result;
            do
            {
                var buffer = _decompressed.Buffer.RequestSegment(10 * 1024); // 10 KB
                result = await client.ReceiveAsync(buffer, cancelToken).ConfigureAwait(false);
                _decompressed.Buffer.Advance(result.Count);

                if (result.CloseStatus != null)
                    throw new WebSocketClosedException(result.CloseStatus.Value, result.CloseStatusDescription);
            }
            while (!result.EndOfMessage);

            // Deserialize
            var payload = JsonSerializer.Read<RpcPayload>(_decompressed.Buffer.AsReadOnlySpan());

            // Handle result
            HandleEvent(payload, readySignal); // Must be before event so slow user handling can't trigger our timeouts
            ReceivedPayload?.Invoke(payload, new PayloadInfo(_decompressed.Buffer.Length, _decompressed.Buffer.Length));
            return payload;
        }
        private void HandleEvent(RpcPayload evnt, TaskCompletionSource<bool> readySignal)
        {
            if (evnt.Event == RpcEvent.Error)
            {
                var data = evnt.Data as ErrorEvent;
                throw new WumpusRpcException(data.Code, data.Message);
            }
            switch (evnt.Command)
            {
                case RpcCommand.Authenticate:
                    readySignal.SetResult(true);
                    break;
            }
        }

        public void Send(RpcPayload payload)
        {
            if (!_runCts.IsCancellationRequested)
                _sendQueue?.Add(payload);
        }
        private async Task SendAsync(ClientWebSocket client, CancellationToken cancelToken, RpcPayload payload)
        {
            payload.Nonce = Guid.NewGuid();
            var writer = JsonSerializer.Write(payload);
            await client.SendAsync(writer.AsSegment(), WebSocketMessageType.Text, true, cancelToken);
            SentPayload?.Invoke(payload, new PayloadInfo(writer.Length, writer.Length));
        }
    }
}
