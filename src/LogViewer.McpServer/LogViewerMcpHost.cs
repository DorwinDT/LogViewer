using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LogViewer.Core.Services;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using McpServerCore = ModelContextProtocol.Server.McpServer;

namespace LogViewer.McpServer
{
    /// <summary>
    /// In-process MCP server over plain HTTP (no SSE push to the client beyond what a session's own GET
    /// stream carries). Wires <see cref="StreamableHttpServerTransport"/> (from ModelContextProtocol.Core,
    /// netstandard2.0) to a bare <see cref="HttpListener"/> because the host process is .NET Framework 4.8
    /// and cannot run ASP.NET Core/Kestrel (ModelContextProtocol.AspNetCore) in-process.
    /// </summary>
    /// <remarks>
    /// Scope for v1: single local client, read-only tools (no sampling/elicitation), so every POST produces
    /// exactly one synchronous JSON response or a 202 for notifications — never a multi-message SSE reply to
    /// POST. The GET endpoint is wired for spec completeness (a client's optional long-lived push channel)
    /// but nothing in this server currently sends anything down it. Not yet tested against a real MCP client;
    /// expect to iterate once it is exercised live.
    /// </remarks>
    public sealed class LogViewerMcpHost : IDisposable
    {
        private readonly LogViewerMcpTools _tools;
        private readonly Action<Exception, string> _onError;
        private readonly ConcurrentDictionary<string, McpSessionEntry> _sessions = new ConcurrentDictionary<string, McpSessionEntry>();

        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private Task _acceptLoopTask;

        /// <param name="onError">Optional sink for exceptions this server would otherwise swallow (per-request
        /// handling is best-effort: one bad request must not take the listener down). The host app can wire
        /// this to its own logger.</param>
        public LogViewerMcpHost(
            LogProcessingService processing,
            Func<System.Collections.Generic.IReadOnlyList<McpReceiverStatus>> getReceiverStatus,
            Action<Exception, string> onError = null)
        {
            _tools = new LogViewerMcpTools(processing, getReceiverStatus);
            _onError = onError;
        }

        public bool IsRunning => _listener != null;

        public void Start(int port)
        {
            if (_listener != null)
                return;

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            _cts = new CancellationTokenSource();
            _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_listener, _cts.Token));
        }

        public async Task StopAsync()
        {
            var listener = _listener;
            if (listener == null)
                return;

            _cts.Cancel();
            try { listener.Stop(); } catch (Exception) { /* already stopping */ }

            try { await _acceptLoopTask.ConfigureAwait(false); } catch (Exception) { /* loop already unwound */ }

            foreach (var sessionId in _sessions.Keys)
            {
                McpSessionEntry entry;
                if (_sessions.TryRemove(sessionId, out entry))
                    await DisposeEntryAsync(entry).ConfigureAwait(false);
            }

            try { listener.Close(); } catch (Exception) { /* ignore */ }
            _listener = null;
        }

        public void Dispose()
        {
            StopAsync().GetAwaiter().GetResult();
        }

        private async Task AcceptLoopAsync(HttpListener listener, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    break; // listener stopped/disposed
                }

                _ = Task.Run(() => HandleRequestAsync(context, token));
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken token)
        {
            var request = context.Request;
            var response = context.Response;
            try
            {
                string sessionId = request.Headers["Mcp-Session-Id"];
                switch (request.HttpMethod)
                {
                    case "POST":
                        await HandlePostAsync(request, response, sessionId, token).ConfigureAwait(false);
                        break;
                    case "GET":
                        await HandleGetAsync(response, sessionId, token).ConfigureAwait(false);
                        return; // response lifetime is owned by the SSE loop
                    case "DELETE":
                        HandleDelete(sessionId);
                        response.StatusCode = 200;
                        break;
                    default:
                        response.StatusCode = 405;
                        break;
                }
            }
            catch (Exception e)
            {
                _onError?.Invoke(e, "Error handling MCP request");
                try { response.StatusCode = 500; } catch (Exception) { /* headers already sent */ }
            }
            finally
            {
                try { response.Close(); } catch (Exception) { /* client already gone */ }
            }
        }

        private async Task HandlePostAsync(HttpListenerRequest request, HttpListenerResponse response, string sessionId, CancellationToken token)
        {
            JsonRpcMessage message;
            try
            {
                message = await JsonSerializer.DeserializeAsync<JsonRpcMessage>(request.InputStream, McpJsonUtilities.DefaultOptions, token)
                    .ConfigureAwait(false);
            }
            catch (JsonException)
            {
                response.StatusCode = 400;
                return;
            }

            if (message == null)
            {
                response.StatusCode = 400;
                return;
            }

            McpSessionEntry entry;
            if (string.IsNullOrEmpty(sessionId) || !_sessions.TryGetValue(sessionId, out entry))
            {
                sessionId = Guid.NewGuid().ToString("N");
                entry = CreateSession(sessionId, token);
                _sessions[sessionId] = entry;
            }

            response.Headers["Mcp-Session-Id"] = sessionId;

            using (var buffer = new MemoryStream())
            {
                bool wroteResponse = await entry.Transport.HandlePostRequestAsync(message, buffer, token).ConfigureAwait(false);
                if (wroteResponse)
                {
                    response.ContentType = "application/json";
                    response.StatusCode = 200;
                    buffer.Position = 0;
                    await buffer.CopyToAsync(response.OutputStream).ConfigureAwait(false);
                }
                else
                {
                    response.StatusCode = 202;
                }
            }
        }

        private async Task HandleGetAsync(HttpListenerResponse response, string sessionId, CancellationToken token)
        {
            McpSessionEntry entry;
            if (string.IsNullOrEmpty(sessionId) || !_sessions.TryGetValue(sessionId, out entry))
            {
                response.StatusCode = 404;
                response.Close();
                return;
            }

            response.ContentType = "text/event-stream";
            response.StatusCode = 200;
            try
            {
                await entry.Transport.HandleGetRequestAsync(response.OutputStream, token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // client disconnected or session torn down: nothing more to do
            }
            finally
            {
                try { response.Close(); } catch (Exception) { /* client already gone */ }
            }
        }

        private void HandleDelete(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return;
            McpSessionEntry entry;
            if (_sessions.TryRemove(sessionId, out entry))
                _ = DisposeEntryAsync(entry);
        }

        private McpSessionEntry CreateSession(string sessionId, CancellationToken parentToken)
        {
            var transport = new StreamableHttpServerTransport { SessionId = sessionId };
            var server = McpServerCore.Create(transport, BuildServerOptions());
            var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
            var entry = new McpSessionEntry { Transport = transport, Server = server, Cts = sessionCts };
            entry.RunTask = Task.Run(() => RunSessionAsync(sessionId, entry), sessionCts.Token);
            return entry;
        }

        private async Task RunSessionAsync(string sessionId, McpSessionEntry entry)
        {
            try
            {
                await entry.Server.RunAsync(entry.Cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // session stopped (host shutdown or client disconnect)
            }
            catch (Exception e)
            {
                _onError?.Invoke(e, "MCP session ended with an error");
            }
            finally
            {
                McpSessionEntry removed;
                _sessions.TryRemove(sessionId, out removed);
                await DisposeEntryAsync(entry).ConfigureAwait(false);
            }
        }

        private static async Task DisposeEntryAsync(McpSessionEntry entry)
        {
            if (entry == null)
                return;
            try { entry.Cts.Cancel(); } catch (Exception) { /* already cancelled */ }
            try { await entry.Transport.DisposeAsync().ConfigureAwait(false); } catch (Exception) { /* already disposed */ }
            try { await entry.Server.DisposeAsync().ConfigureAwait(false); } catch (Exception) { /* already disposed */ }
            entry.Cts.Dispose();
        }

        private McpServerOptions BuildServerOptions()
        {
            var toolCollection = new McpServerPrimitiveCollection<ModelContextProtocol.Server.McpServerTool>();
            toolCollection.Add(CreateTool(nameof(LogViewerMcpTools.SearchLogs), "search_logs"));
            toolCollection.Add(CreateTool(nameof(LogViewerMcpTools.GetLogStatistics), "get_log_statistics"));
            toolCollection.Add(CreateTool(nameof(LogViewerMcpTools.GetReceiverStatus), "get_receiver_status"));

            return new McpServerOptions
            {
                ServerInfo = new Implementation { Name = "LogViewer", Version = "1.0.0" },
                Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
                ToolCollection = toolCollection
            };
        }

        private ModelContextProtocol.Server.McpServerTool CreateTool(string methodName, string toolName)
        {
            MethodInfo method = typeof(LogViewerMcpTools).GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
            return ModelContextProtocol.Server.McpServerTool.Create(method, _tools, new McpServerToolCreateOptions { Name = toolName });
        }

        private sealed class McpSessionEntry
        {
            public StreamableHttpServerTransport Transport;
            public McpServerCore Server;
            public CancellationTokenSource Cts;
            public Task RunTask;
        }
    }
}
