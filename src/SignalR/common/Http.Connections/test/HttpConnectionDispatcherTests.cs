// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Connections.Internal;
using Microsoft.AspNetCore.Http.Connections.Internal.Transports;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Internal;
using Microsoft.AspNetCore.SignalR.Tests;
using Microsoft.AspNetCore.Testing;
using Microsoft.AspNetCore.Testing.xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Microsoft.AspNetCore.Http.Connections.Tests
{
    public class HttpConnectionDispatcherTests : VerifiableLoggedTest
    {
        [Fact]
        public async Task NegotiateVersionZeroReservesConnectionIdAndReturnsIt()
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);
                var context = new DefaultHttpContext();
                var services = new ServiceCollection();
                services.AddSingleton<TestConnectionHandler>();
                services.AddOptions();
                var ms = new MemoryStream();
                context.Request.Path = "/foo";
                context.Request.Method = "POST";
                context.Response.Body = ms;
                await dispatcher.ExecuteNegotiateAsync(context, new HttpConnectionDispatcherOptions());
                var negotiateResponse = JsonConvert.DeserializeObject<JObject>(Encoding.UTF8.GetString(ms.ToArray()));
                var connectionId = negotiateResponse.Value<string>("connectionId");
                var connectionToken = negotiateResponse.Value<string>("connectionToken");
                Assert.Null(connectionToken);
                Assert.NotNull(connectionId);
            }
        }

        [Fact]
        public async Task NegotiateReservesConnectionTokenAndConnectionIdAndReturnsIt()
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);
                var context = new DefaultHttpContext();
                var services = new ServiceCollection();
                services.AddSingleton<TestConnectionHandler>();
                services.AddOptions();
                var ms = new MemoryStream();
                context.Request.Path = "/foo";
                context.Request.Method = "POST";
                context.Response.Body = ms;
                context.Request.QueryString = new QueryString("?negotiateVersion=1");
                await dispatcher.ExecuteNegotiateAsync(context, new HttpConnectionDispatcherOptions());
                var negotiateResponse = JsonConvert.DeserializeObject<JObject>(Encoding.UTF8.GetString(ms.ToArray()));
                var connectionId = negotiateResponse.Value<string>("connectionId");
                var connectionToken = negotiateResponse.Value<string>("connectionToken");
                Assert.True(manager.TryGetConnection(connectionToken, out var connectionContext));
                Assert.Equal(connectionId, connectionContext.ConnectionId);
                Assert.NotEqual(connectionId, connectionToken);
            }
        }

        [Fact]
        public async Task CheckThatThresholdValuesAreEnforced()
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);
                var context = new DefaultHttpContext();
                var services = new ServiceCollection();
                services.AddSingleton<TestConnectionHandler>();
                services.AddOptions();
                var ms = new MemoryStream();
                context.Request.Path = "/foo";
                context.Request.Method = "POST";
                context.Response.Body = ms;
                context.Request.QueryString = new QueryString("?negotiateVersion=1");
                var options = new HttpConnectionDispatcherOptions { TransportMaxBufferSize = 4, ApplicationMaxBufferSize = 4 };
                await dispatcher.ExecuteNegotiateAsync(context, options);
                var negotiateResponse = JsonConvert.DeserializeObject<JObject>(Encoding.UTF8.GetString(ms.ToArray()));
                var connectionToken = negotiateResponse.Value<string>("connectionToken");
                context.Request.QueryString = context.Request.QueryString.Add("id", connectionToken);
                Assert.True(manager.TryGetConnection(connectionToken, out var connection));
                // Fake actual connection after negotiate to populate the pipes on the connection
                await dispatcher.ExecuteAsync(context, options, c => Task.CompletedTask);

                // This write should complete immediately but it exceeds the writer threshold
                var writeTask = connection.Application.Output.WriteAsync(new[] { (byte)'b', (byte)'y', (byte)'t', (byte)'e', (byte)'s' });

                Assert.False(writeTask.IsCompleted);

                // Reading here puts us below the threshold
                await connection.Transport.Input.ConsumeAsync(5);

                await writeTask.AsTask().OrTimeout();
            }
        }

        [Fact]
        public async Task InvalidNegotiateProtocolVersionThrows()
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);
                var context = new DefaultHttpContext();
                var services = new ServiceCollection();
                services.AddSingleton<TestConnectionHandler>();
                services.AddOptions();
                var ms = new MemoryStream();
                context.Request.Path = "/foo";
                context.Request.Method = "POST";
                context.Response.Body = ms;
                context.Request.QueryString = new QueryString("?negotiateVersion=Invalid");
                var options = new HttpConnectionDispatcherOptions { TransportMaxBufferSize = 4, ApplicationMaxBufferSize = 4 };
                await dispatcher.ExecuteNegotiateAsync(context, options);
                var negotiateResponse = JsonConvert.DeserializeObject<JObject>(Encoding.UTF8.GetString(ms.ToArray()));

                var error = negotiateResponse.Value<string>("error");
                Assert.Equal("The client requested an invalid protocol version 'Invalid'", error);

                var connectionId = negotiateResponse.Value<string>("connectionId");
                Assert.Null(connectionId);
            }
        }

        [Fact]
        public async Task NoNegotiateVersionInQueryStringThrowsWhenMinProtocolVersionIsSet()
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);
                var context = new DefaultHttpContext();
                var services = new ServiceCollection();
                services.AddSingleton<TestConnectionHandler>();
                services.AddOptions();
                var ms = new MemoryStream();
                context.Request.Path = "/foo";
                context.Request.Method = "POST";
                context.Response.Body = ms;
                context.Request.QueryString = new QueryString("");
                var options = new HttpConnectionDispatcherOptions { TransportMaxBufferSize = 4, ApplicationMaxBufferSize = 4, MinimumProtocolVersion = 1 };
                await dispatcher.ExecuteNegotiateAsync(context, options);
                var negotiateResponse = JsonConvert.DeserializeObject<JObject>(Encoding.UTF8.GetString(ms.ToArray()));

                var error = negotiateResponse.Value<string>("error");
                Assert.Equal("The client requested version '0', but the server does not support this version.", error);

                var connectionId = negotiateResponse.Value<string>("connectionId");
                Assert.Null(connectionId);
            }
        }

        [Theory]
        [InlineData(HttpTransportType.LongPolling)]
        [InlineData(HttpTransportType.ServerSentEvents)]
        public async Task CheckThatThresholdValuesAreEnforcedWithSends(HttpTransportType transportType)
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);
                var pipeOptions = new PipeOptions(pauseWriterThreshold: 8, resumeWriterThreshold: 4);
                var connection = manager.CreateConnection(pipeOptions, pipeOptions);
                connection.TransportType = transportType;

                using (var requestBody = new MemoryStream())
                using (var responseBody = new MemoryStream())
                {
                    var bytes = Encoding.UTF8.GetBytes("EXTRADATA Hi");
                    requestBody.Write(bytes, 0, bytes.Length);
                    requestBody.Seek(0, SeekOrigin.Begin);

                    var context = new DefaultHttpContext();
                    context.Request.Body = requestBody;
                    context.Response.Body = responseBody;

                    var services = new ServiceCollection();
                    services.AddSingleton<TestConnectionHandler>();
                    services.AddOptions();
                    context.Request.Path = "/foo";
                    context.Request.Method = "POST";
                    var values = new Dictionary<string, StringValues>();
                    values["id"] = connection.ConnectionToken;
                    values["negotiateVersion"] = "1";
                    var qs = new QueryCollection(values);
                    context.Request.Query = qs;

                    var builder = new ConnectionBuilder(services.BuildServiceProvider());
                    builder.UseConnectionHandler<TestConnectionHandler>();
                    var app = builder.Build();

                    // This task should complete immediately but it exceeds the writer threshold
                    var executeTask = dispatcher.ExecuteAsync(context, new HttpConnectionDispatcherOptions(), app);
                    Assert.False(executeTask.IsCompleted);
                    await connection.Transport.Input.ConsumeAsync(10);
                    await executeTask.OrTimeout();

                    Assert.True(connection.Transport.Input.TryRead(out var result));
                    Assert.Equal("Hi", Encoding.UTF8.GetString(result.Buffer.ToArray()));
                    connection.Transport.Input.AdvanceTo(result.Buffer.End);
                }
            }
        }

        [Theory]
        [InlineData(HttpTransportType.LongPolling | HttpTransportType.WebSockets | HttpTransportType.ServerSentEvents)]
        [InlineData(HttpTransportType.None)]
        [InlineData(HttpTransportType.LongPolling | HttpTransportType.WebSockets)]
        public async Task NegotiateReturnsAvailableTransportsAfterFilteringByOptions(HttpTransportType transports)
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);
                var context = new DefaultHttpContext();
                context.Features.Set<IHttpResponseFeature>(new ResponseFeature());
                context.Features.Set<IHttpWebSocketFeature>(new TestWebSocketConnectionFeature());
                var services = new ServiceCollection();
                services.AddSingleton<TestConnectionHandler>();
                services.AddOptions();
                var ms = new MemoryStream();
                context.Request.Path = "/foo";
                context.Request.Method = "POST";
                context.Response.Body = ms;
                context.Request.QueryString = new QueryString("?negotiateVersion=1");
                await dispatcher.ExecuteNegotiateAsync(context, new HttpConnectionDispatcherOptions { Transports = transports });

                var negotiateResponse = JsonConvert.DeserializeObject<JObject>(Encoding.UTF8.GetString(ms.ToArray()));
                var availableTransports = HttpTransportType.None;
                foreach (var transport in negotiateResponse["availableTransports"])
                {
                    var transportType = (HttpTransportType)Enum.Parse(typeof(HttpTransportType), transport.Value<string>("transport"));
                    availableTransports |= transportType;
                }

                Assert.Equal(transports, availableTransports);
            }
        }

        [Theory]
        [InlineData(HttpTransportType.WebSockets)]
        [InlineData(HttpTransportType.ServerSentEvents)]
        [InlineData(HttpTransportType.LongPolling)]
        public async Task EndpointsThatAcceptConnectionId404WhenUnknownConnectionIdProvided(HttpTransportType transportType)
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);

                using (var strm = new MemoryStream())
                {
                    var context = new DefaultHttpContext();
                    context.Features.Set<IHttpResponseFeature>(new ResponseFeature());
                    context.Response.Body = strm;

                    var services = new ServiceCollection();
                    services.AddSingleton<TestConnectionHandler>();
                    services.AddOptions();
                    context.Request.Path = "/foo";
                    context.Request.Method = "GET";
                    var values = new Dictionary<string, StringValues>();
                    values["id"] = "unknown";
                    values["negotiateVersion"] = "1";
                    var qs = new QueryCollection(values);
                    context.Request.Query = qs;
                    SetTransport(context, transportType);

                    var builder = new ConnectionBuilder(services.BuildServiceProvider());
                    builder.UseConnectionHandler<TestConnectionHandler>();
                    var app = builder.Build();
                    await dispatcher.ExecuteAsync(context, new HttpConnectionDispatcherOptions(), app);

                    Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
                    await strm.FlushAsync();
                    Assert.Equal("No Connection with that ID", Encoding.UTF8.GetString(strm.ToArray()));
                }
            }
        }

        [Fact]
        public async Task EndpointsThatAcceptConnectionId404WhenUnknownConnectionIdProvidedForPost()
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);

                using (var strm = new MemoryStream())
                {
                    var context = new DefaultHttpContext();
                    context.Response.Body = strm;

                    var services = new ServiceCollection();
                    services.AddSingleton<TestConnectionHandler>();
                    services.AddOptions();
                    context.Request.Path = "/foo";
                    context.Request.Method = "POST";
                    var values = new Dictionary<string, StringValues>();
                    values["id"] = "unknown";
                    values["negotiateVersion"] = "1";
                    var qs = new QueryCollection(values);
                    context.Request.Query = qs;

                    var builder = new ConnectionBuilder(services.BuildServiceProvider());
                    builder.UseConnectionHandler<TestConnectionHandler>();
                    var app = builder.Build();
                    await dispatcher.ExecuteAsync(context, new HttpConnectionDispatcherOptions(), app);

                    Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
                    await strm.FlushAsync();
                    Assert.Equal("No Connection with that ID", Encoding.UTF8.GetString(strm.ToArray()));
                }
            }
        }

        [Fact]
        public async Task PostNotAllowedForWebSocketConnections()
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);
                var connection = manager.CreateConnection();
                connection.TransportType = HttpTransportType.WebSockets;

                using (var strm = new MemoryStream())
                {
                    var context = new DefaultHttpContext();
                    context.Response.Body = strm;

                    var services = new ServiceCollection();
                    services.AddSingleton<TestConnectionHandler>();
                    services.AddOptions();
                    context.Request.Path = "/foo";
                    context.Request.Method = "POST";
                    var values = new Dictionary<string, StringValues>();
                    values["id"] = connection.ConnectionToken;
                    values["negotiateVersion"] = "1";
                    var qs = new QueryCollection(values);
                    context.Request.Query = qs;

                    var builder = new ConnectionBuilder(services.BuildServiceProvider());
                    builder.UseConnectionHandler<TestConnectionHandler>();
                    var app = builder.Build();
                    await dispatcher.ExecuteAsync(context, new HttpConnectionDispatcherOptions(), app);

                    Assert.Equal(StatusCodes.Status405MethodNotAllowed, context.Response.StatusCode);
                    await strm.FlushAsync();
                    Assert.Equal("POST requests are not allowed for WebSocket connections.", Encoding.UTF8.GetString(strm.ToArray()));
                }
            }
        }

        [Fact]
        public async Task PostReturns404IfConnectionDisposed()
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);
                var connection = manager.CreateConnection();
                connection.TransportType = HttpTransportType.LongPolling;
                await connection.DisposeAsync(closeGracefully: false);

                using (var strm = new MemoryStream())
                {
                    var context = new DefaultHttpContext();
                    context.Response.Body = strm;

                    var services = new ServiceCollection();
                    services.AddSingleton<TestConnectionHandler>();
                    services.AddOptions();
                    context.Request.Path = "/foo";
                    context.Request.Method = "POST";
                    var values = new Dictionary<string, StringValues>();
                    values["id"] = connection.ConnectionId;
                    values["negotiateVersion"] = "1";
                    var qs = new QueryCollection(values);
                    context.Request.Query = qs;

                    var builder = new ConnectionBuilder(services.BuildServiceProvider());
                    builder.UseConnectionHandler<TestConnectionHandler>();
                    var app = builder.Build();
                    await dispatcher.ExecuteAsync(context, new HttpConnectionDispatcherOptions(), app);

                    Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
                }
            }
        }

        [Theory]
        [InlineData(HttpTransportType.ServerSentEvents)]
        [InlineData(HttpTransportType.WebSockets)]
        public async Task TransportEndingGracefullyWaitsOnApplication(HttpTransportType transportType)
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);
                var connection = manager.CreateConnection();
                connection.TransportType = transportType;

                using (var strm = new MemoryStream())
                {
                    var context = new DefaultHttpContext();
                    SetTransport(context, transportType);
                    var cts = new CancellationTokenSource();
                    context.Response.Body = strm;
                    context.RequestAborted = cts.Token;

                    var services = new ServiceCollection();
                    services.AddSingleton<TestConnectionHandler>();
                    services.AddOptions();
                    context.Request.Path = "/foo";
                    context.Request.Method = "GET";
                    var values = new Dictionary<string, StringValues>();
                    values["id"] = connection.ConnectionToken;
                    values["negotiateVersion"] = "1";
                    var qs = new QueryCollection(values);
                    context.Request.Query = qs;

                    var builder = new ConnectionBuilder(services.BuildServiceProvider());
                    builder.Use(next =>
                    {
                        return async connectionContext =>
                        {
                            // Ensure both sides of the pipe are ok
                            var result = await connectionContext.Transport.Input.ReadAsync();
                            Assert.True(result.IsCompleted);
                            await connectionContext.Transport.Output.WriteAsync(result.Buffer.First);
                        };
                    });

                    var app = builder.Build();
                    var task = dispatcher.ExecuteAsync(context, new HttpConnectionDispatcherOptions(), app);

                    // Pretend the transport closed because the client disconnected
                    if (context.WebSockets.IsWebSocketRequest)
                    {
                        var ws = (TestWebSocketConnectionFeature)context.Features.Get<IHttpWebSocketFeature>();
                        await ws.Client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", default);
                    }
                    else
                    {
                        cts.Cancel();
                    }

                    await task.OrTimeout();

                    await connection.ApplicationTask.OrTimeout();
                }
            }
        }

        [Fact]
        public async Task TransportEndingGracefullyWaitsOnApplicationLongPolling()
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory, TimeSpan.FromSeconds(5));
                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);
                var connection = manager.CreateConnection();
                connection.TransportType = HttpTransportType.LongPolling;

                using (var strm = new MemoryStream())
                {
                    var context = new DefaultHttpContext();
                    SetTransport(context, HttpTransportType.LongPolling);
                    var cts = new CancellationTokenSource();
                    context.Response.Body = strm;
                    context.RequestAborted = cts.Token;

                    var services = new ServiceCollection();
                    services.AddSingleton<TestConnectionHandler>();
                    services.AddOptions();
                    context.Request.Path = "/foo";
                    context.Request.Method = "GET";
                    var values = new Dictionary<string, StringValues>();
                    values["id"] = connection.ConnectionToken;
                    values["negotiateVersion"] = "1";
                    var qs = new QueryCollection(values);
                    context.Request.Query = qs;

                    var builder = new ConnectionBuilder(services.BuildServiceProvider());
                    builder.Use(next =>
                    {
                        return async connectionContext =>
                        {
                            // Ensure both sides of the pipe are ok
                            var result = await connectionContext.Transport.Input.ReadAsync();
                            Assert.True(result.IsCompleted);
                            await connectionContext.Transport.Output.WriteAsync(result.Buffer.First);
                        };
                    });

                    var app = builder.Build();
                    var task = dispatcher.ExecuteAsync(context, new HttpConnectionDispatcherOptions(), app);

                    // Pretend the transport closed because the client disconnected
                    cts.Cancel();

                    await task.OrTimeout();

                    // We've been gone longer than the expiration time
                    connection.LastSeenUtc = DateTime.UtcNow.Subtract(TimeSpan.FromSeconds(10));

                    // The application is still running here because the poll is only killed
                    // by the heartbeat so we pretend to do a scan and this should force the application task to complete
                    manager.Scan();

                    // The application task should complete gracefully
                    await connection.ApplicationTask.OrTimeout();
                }
            }
        }

        [Theory]
        [InlineData(HttpTransportType.LongPolling)]
        [InlineData(HttpTransportType.ServerSentEvents)]
        public async Task PostSendsToConnection(HttpTransportType transportType)
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);
                var connection = manager.CreateConnection();
                connection.TransportType = transportType;

                using (var requestBody = new MemoryStream())
                using (var responseBody = new MemoryStream())
                {
                    var bytes = Encoding.UTF8.GetBytes("Hello World");
                    requestBody.Write(bytes, 0, bytes.Length);
                    requestBody.Seek(0, SeekOrigin.Begin);

                    var context = new DefaultHttpContext();
                    context.Request.Body = requestBody;
                    context.Response.Body = responseBody;

                    var services = new ServiceCollection();
                    services.AddSingleton<TestConnectionHandler>();
                    services.AddOptions();
                    context.Request.Path = "/foo";
                    context.Request.Method = "POST";
                    var values = new Dictionary<string, StringValues>();
                    values["id"] = connection.ConnectionToken;
                    values["negotiateVersion"] = "1";
                    var qs = new QueryCollection(values);
                    context.Request.Query = qs;

                    var builder = new ConnectionBuilder(services.BuildServiceProvider());
                    builder.UseConnectionHandler<TestConnectionHandler>();
                    var app = builder.Build();

                    Assert.Equal(0, connection.ApplicationStream.Length);

                    await dispatcher.ExecuteAsync(context, new HttpConnectionDispatcherOptions(), app);

                    Assert.True(connection.Transport.Input.TryRead(out var result));
                    Assert.Equal("Hello World", Encoding.UTF8.GetString(result.Buffer.ToArray()));
                    Assert.Equal(0, connection.ApplicationStream.Length);
                    connection.Transport.Input.AdvanceTo(result.Buffer.End);
                }
            }
        }

        [Theory]
        [InlineData(HttpTransportType.LongPolling)]
        [InlineData(HttpTransportType.ServerSentEvents)]
        public async Task PostSendsToConnectionInParallel(HttpTransportType transportType)
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);
                var connection = manager.CreateConnection();
                connection.TransportType = transportType;

                // Allow a maximum of one caller to use code at one time
                var callerTracker = new SemaphoreSlim(1, 1);
                var waitTcs = new TaskCompletionSource<bool>();

                // This tests thread safety of sending multiple pieces of data to a connection at once
                var executeTask1 = DispatcherExecuteAsync(dispatcher, connection, callerTracker, waitTcs.Task);
                var executeTask2 = DispatcherExecuteAsync(dispatcher, connection, callerTracker, waitTcs.Task);

                waitTcs.SetResult(true);

                await Task.WhenAll(executeTask1, executeTask2);
            }

            async Task DispatcherExecuteAsync(HttpConnectionDispatcher dispatcher, HttpConnectionContext connection, SemaphoreSlim callerTracker, Task waitTask)
            {
                using (var requestBody = new TrackingMemoryStream(callerTracker, waitTask))
                {
                    var bytes = Encoding.UTF8.GetBytes("Hello World");
                    requestBody.Write(bytes, 0, bytes.Length);
                    requestBody.Seek(0, SeekOrigin.Begin);

                    var context = new DefaultHttpContext();
                    context.Request.Body = requestBody;

                    var services = new ServiceCollection();
                    services.AddSingleton<TestConnectionHandler>();
                    services.AddOptions();
                    context.Request.Path = "/foo";
                    context.Request.Method = "POST";
                    var values = new Dictionary<string, StringValues>();
                    values["id"] = connection.ConnectionId;
                    values["negotiateVersion"] = "1";
                    var qs = new QueryCollection(values);
                    context.Request.Query = qs;

                    var builder = new ConnectionBuilder(services.BuildServiceProvider());
                    builder.UseConnectionHandler<TestConnectionHandler>();
                    var app = builder.Build();

                    await dispatcher.ExecuteAsync(context, new HttpConnectionDispatcherOptions(), app);
                }
            }
        }

        private class TrackingMemoryStream : MemoryStream
        {
            private readonly SemaphoreSlim _callerTracker;
            private readonly Task _waitTask;

            public TrackingMemoryStream(SemaphoreSlim callerTracker, Task waitTask)
            {
                _callerTracker = callerTracker;
                _waitTask = waitTask;
            }

            public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
            {
                // Will return false if all available locks from semaphore are taken
                if (!_callerTracker.Wait(0))
                {
                    throw new Exception("Too many callers.");
                }

                try
                {
                    await _waitTask;

                    await base.CopyToAsync(destination, bufferSize, cancellationToken);
                }
                finally
                {
                    _callerTracker.Release();
                }
            }
        }

        [Fact]
        public async Task HttpContextFeatureForLongpollingWorksBetweenPolls()
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);
                var connection = manager.CreateConnection();
                connection.TransportType = HttpTransportType.LongPolling;

                using (var requestBody = new MemoryStream())
                using (var responseBody = new MemoryStream())
                {
                    var context = new DefaultHttpContext();
                    context.Request.Body = requestBody;
                    context.Response.Body = responseBody;

                    var services = new ServiceCollection();
                    services.AddSingleton<HttpContextConnectionHandler>();
                    services.AddOptions();

                    // Setup state on the HttpContext
                    context.Request.Path = "/foo";
                    context.Request.Method = "GET";
                    var values = new Dictionary<string, StringValues>();
                    values["id"] = connection.ConnectionToken;
                    values["negotiateVersion"] = "1";
                    values["another"] = "value";
                    var qs = new QueryCollection(values);
                    context.Request.Query = qs;
                    context.Request.Headers["header1"] = "h1";
                    context.Request.Headers["header2"] = "h2";
                    context.Request.Headers["header3"] = "h3";
                    context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("claim1", "claimValue") }));
                    context.TraceIdentifier = "requestid";
                    context.Connection.Id = "connectionid";
                    context.Connection.LocalIpAddress = IPAddress.Loopback;
                    context.Connection.LocalPort = 4563;
                    context.Connection.RemoteIpAddress = IPAddress.IPv6Any;
                    context.Connection.RemotePort = 43456;
                    context.SetEndpoint(new Endpoint(null, null, "TestName"));

                    var builder = new ConnectionBuilder(services.BuildServiceProvider());
                    builder.UseConnectionHandler<HttpContextConnectionHandler>();
                    var app = builder.Build();

                    // Start a poll
                    var task = dispatcher.ExecuteAsync(context, new HttpConnectionDispatcherOptions(), app);
                    Assert.True(task.IsCompleted);
                    Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);

                    task = dispatcher.ExecuteAsync(context, new HttpConnectionDispatcherOptions(), app);

                    // Send to the application
                    var buffer = Encoding.UTF8.GetBytes("Hello World");
                    await connection.Application.Output.WriteAsync(buffer);

                    // The poll request should end
                    await task;

                    // Make sure the actual response isn't affected
                    Assert.Equal("application/octet-stream", context.Response.ContentType);

                    // Now do a new send again without the poll (that request should have ended)
                    await connection.Application.Output.WriteAsync(buffer);

                    connection.Application.Output.Complete();

                    // Wait for the endpoint to end
                    await connection.ApplicationTask;

                    var connectionHttpContext = connection.GetHttpContext();
                    Assert.NotNull(connectionHttpContext);

                    Assert.Equal(3, connectionHttpContext.Request.Query.Count);
                    Assert.Equal("value", connectionHttpContext.Request.Query["another"]);

                    Assert.Equal(3, connectionHttpContext.Request.Headers.Count);
                    Assert.Equal("h1", connectionHttpContext.Request.Headers["header1"]);
                    Assert.Equal("h2", connectionHttpContext.Request.Headers["header2"]);
                    Assert.Equal("h3", connectionHttpContext.Request.Headers["header3"]);
                    Assert.Equal("requestid", connectionHttpContext.TraceIdentifier);
                    Assert.Equal("claimValue", connectionHttpContext.User.Claims.FirstOrDefault().Value);
                    Assert.Equal("connectionid", connectionHttpContext.Connection.Id);
                    Assert.Equal(IPAddress.Loopback, connectionHttpContext.Connection.LocalIpAddress);
                    Assert.Equal(4563, connectionHttpContext.Connection.LocalPort);
                    Assert.Equal(IPAddress.IPv6Any, connectionHttpContext.Connection.RemoteIpAddress);
                    Assert.Equal(43456, connectionHttpContext.Connection.RemotePort);
                    Assert.NotNull(connectionHttpContext.RequestServices);
                    Assert.Equal(Stream.Null, connectionHttpContext.Response.Body);
                    Assert.NotNull(connectionHttpContext.Response.Headers);
                    Assert.Equal("application/xml", connectionHttpContext.Response.ContentType);
                    var endpointFeature = connectionHttpContext.Features.Get<IEndpointFeature>();
                    Assert.NotNull(endpointFeature);
                    Assert.Equal("TestName", endpointFeature.Endpoint.DisplayName);
                }
            }
        }

        [Theory]
        [InlineData(HttpTransportType.ServerSentEvents)]
        [InlineData(HttpTransportType.LongPolling)]
        public async Task EndpointsThatRequireConnectionId400WhenNoConnectionIdProvided(HttpTransportType transportType)
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);
                using (var strm = new MemoryStream())
                {
                    var context = new DefaultHttpContext();
                    context.Features.Set<IHttpResponseFeature>(new ResponseFeature());
                    context.Response.Body = strm;
                    var services = new ServiceCollection();
                    services.AddOptions();
                    services.AddSingleton<TestConnectionHandler>();
                    context.Request.Path = "/foo";
                    context.Request.Method = "GET";
                    context.Request.QueryString = new QueryString("?negotiateVersion=1");

                    SetTransport(context, transportType);

                    var builder = new ConnectionBuilder(services.BuildServiceProvider());
                    builder.UseConnectionHandler<TestConnectionHandler>();
                    var app = builder.Build();
                    await dispatcher.ExecuteAsync(context, new HttpConnectionDispatcherOptions(), app);

                    Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
                    await strm.FlushAsync();
                    Assert.Equal("Connection ID required", Encoding.UTF8.GetString(strm.ToArray()));
                }
            }
        }

        [Theory]
        [InlineData(HttpTransportType.LongPolling)]
        [InlineData(HttpTransportType.ServerSentEvents)]
        public async Task IOExceptionWhenReadingRequestReturns400Response(HttpTransportType transportType)
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);
                var connection = manager.CreateConnection();
                connection.TransportType = transportType;

                var mockStream = new Mock<Stream>();
                mockStream.Setup(m => m.CopyToAsync(It.IsAny<Stream>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).Throws(new IOException());

                using (var responseBody = new MemoryStream())
                {
                    var context = new DefaultHttpContext();
                    context.Request.Body = mockStream.Object;
                    context.Response.Body = responseBody;

                    var services = new ServiceCollection();
                    services.AddSingleton<TestConnectionHandler>();
                    services.AddOptions();
                    context.Request.Path = "/foo";
                    context.Request.Method = "POST";
                    var values = new Dictionary<string, StringValues>();
                    values["id"] = connection.ConnectionToken;
                    values["negotiateVersion"] = "1";
                    var qs = new QueryCollection(values);
                    context.Request.Query = qs;

                    await dispatcher.ExecuteAsync(context, new HttpConnectionDispatcherOptions(), c => Task.CompletedTask);

                    Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
                }
            }
        }

        [Fact]
        public async Task EndpointsThatRequireConnectionId400WhenNoConnectionIdProvidedForPost()
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);
                using (var strm = new MemoryStream())
                {
                    var context = new DefaultHttpContext();
                    context.Response.Body = strm;
                    var services = new ServiceCollection();
                    services.AddOptions();
                    services.AddSingleton<TestConnectionHandler>();
                    context.Request.Path = "/foo";
                    context.Request.Method = "POST";
                    context.Request.QueryString = new QueryString("?negotiateVersion=1");

                    var builder = new ConnectionBuilder(services.BuildServiceProvider());
                    builder.UseConnectionHandler<TestConnectionHandler>();
                    var app = builder.Build();
                    await dispatcher.ExecuteAsync(context, new HttpConnectionDispatcherOptions(), app);

                    Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
                    await strm.FlushAsync();
                    Assert.Equal("Connection ID required", Encoding.UTF8.GetString(strm.ToArray()));
                }
            }
        }

        [Theory]
        [InlineData(HttpTransportType.LongPolling, 200)]
        [InlineData(HttpTransportType.WebSockets, 404)]
        [InlineData(HttpTransportType.ServerSentEvents, 404)]
        public async Task EndPointThatOnlySupportsLongPollingRejectsOtherTransports(HttpTransportType transportType, int status)
        {
            using (StartVerifiableLog())
            {
                await CheckTransportSupported(HttpTransportType.LongPolling, transportType, status, LoggerFactory);
            }
        }

        [Theory]
        [InlineData(HttpTransportType.ServerSentEvents, 200)]
        [InlineData(HttpTransportType.WebSockets, 404)]
        [InlineData(HttpTransportType.LongPolling, 404)]
        public async Task EndPointThatOnlySupportsSSERejectsOtherTransports(HttpTransportType transportType, int status)
        {
            using (StartVerifiableLog())
            {
                await CheckTransportSupported(HttpTransportType.ServerSentEvents, transportType, status, LoggerFactory);
            }
        }

        [Theory]
        [InlineData(HttpTransportType.WebSockets, 200)]
        [InlineData(HttpTransportType.ServerSentEvents, 404)]
        [InlineData(HttpTransportType.LongPolling, 404)]
        public async Task EndPointThatOnlySupportsWebSockesRejectsOtherTransports(HttpTransportType transportType, int status)
        {
            using (StartVerifiableLog())
            {
                await CheckTransportSupported(HttpTransportType.WebSockets, transportType, status, LoggerFactory);
            }
        }

        [Theory]
        [InlineData(HttpTransportType.LongPolling, 404)]
        public async Task EndPointThatOnlySupportsWebSocketsAndSSERejectsLongPolling(HttpTransportType transportType, int status)
        {
            using (StartVerifiableLog())
            {
                await CheckTransportSupported(HttpTransportType.WebSockets | HttpTransportType.ServerSentEvents, transportType, status, LoggerFactory);
            }
        }

        [Fact]
        public async Task CompletedEndPointEndsConnection()
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var connection = manager.CreateConnection();
                connection.TransportType = HttpTransportType.ServerSentEvents;

                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);

                var context = MakeRequest("/foo", connection);

                SetTransport(context, HttpTransportType.ServerSentEvents);

                var services = new ServiceCollection();
                services.AddSingleton<ImmediatelyCompleteConnectionHandler>();
                var builder = new ConnectionBuilder(services.BuildServiceProvider());
                builder.UseConnectionHandler<ImmediatelyCompleteConnectionHandler>();
                var app = builder.Build();
                await dispatcher.ExecuteAsync(context, new HttpConnectionDispatcherOptions(), app);

                Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);

                var exists = manager.TryGetConnection(connection.ConnectionToken, out _);
                Assert.False(exists);
            }
        }

        [Fact]
        public async Task SynchronusExceptionEndsConnection()
        {
            bool ExpectedErrors(WriteContext writeContext)
            {
                return writeContext.LoggerName == typeof(HttpConnectionManager).FullName &&
                       writeContext.EventId.Name == "FailedDispose";
            }

            using (StartVerifiableLog(expectedErrorsFilter: ExpectedErrors))
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var connection = manager.CreateConnection();
                connection.TransportType = HttpTransportType.ServerSentEvents;

                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);
                var context = MakeRequest("/foo", connection);
                SetTransport(context, HttpTransportType.ServerSentEvents);

                var services = new ServiceCollection();
                services.AddSingleton<SynchronusExceptionConnectionHandler>();
                var builder = new ConnectionBuilder(services.BuildServiceProvider());
                builder.UseConnectionHandler<SynchronusExceptionConnectionHandler>();
                var app = builder.Build();
                await dispatcher.ExecuteAsync(context, new HttpConnectionDispatcherOptions(), app);

                Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);

                var exists = manager.TryGetConnection(connection.ConnectionId, out _);
                Assert.False(exists);
            }
        }

        [Fact]
        public async Task CompletedEndPointEndsLongPollingConnection()
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var connection = manager.CreateConnection();
                connection.TransportType = HttpTransportType.LongPolling;

                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);

                var context = MakeRequest("/foo", connection);

                var services = new ServiceCollection();
                services.AddSingleton<ImmediatelyCompleteConnectionHandler>();
                var builder = new ConnectionBuilder(services.BuildServiceProvider());
                builder.UseConnectionHandler<ImmediatelyCompleteConnectionHandler>();
                var app = builder.Build();
                // First poll will 200
                await dispatcher.ExecuteAsync(context, new HttpConnectionDispatcherOptions(), app);
                Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);

                await dispatcher.ExecuteAsync(context, new HttpConnectionDispatcherOptions(), app);

                Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);

                var exists = manager.TryGetConnection(connection.ConnectionId, out _);
                Assert.False(exists);
            }
        }

        [Fact]
        public async Task LongPollingTimeoutSets200StatusCode()
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var connection = manager.CreateConnection();
                connection.TransportType = HttpTransportType.LongPolling;

                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);

                var context = MakeRequest("/foo", connection);

                var services = new ServiceCollection();
                services.AddSingleton<TestConnectionHandler>();
                var builder = new ConnectionBuilder(services.BuildServiceProvider());
                builder.UseConnectionHandler<TestConnectionHandler>();
                var app = builder.Build();
                var options = new HttpConnectionDispatcherOptions();
                options.LongPolling.PollTimeout = TimeSpan.FromSeconds(2);
                await dispatcher.ExecuteAsync(context, options, app).OrTimeout();

                Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            }
        }

        [Fact]
        [LogLevel(LogLevel.Trace)]
        public async Task WebSocketTransportTimesOutWhenCloseFrameNotReceived()
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var connection = manager.CreateConnection();
                connection.TransportType = HttpTransportType.WebSockets;

                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);

                var context = MakeRequest("/foo", connection);
                SetTransport(context, HttpTransportType.WebSockets);

                var services = new ServiceCollection();
                services.AddSingleton<ImmediatelyCompleteConnectionHandler>();
                var builder = new ConnectionBuilder(services.BuildServiceProvider());
                builder.UseConnectionHandler<ImmediatelyCompleteConnectionHandler>();
                var app = builder.Build();
                var options = new HttpConnectionDispatcherOptions();
                options.WebSockets.CloseTimeout = TimeSpan.FromSeconds(1);

                var task = dispatcher.ExecuteAsync(context, options, app);

                await task.OrTimeout();
            }
        }

        [Theory]
        [InlineData(HttpTransportType.WebSockets)]
        [InlineData(HttpTransportType.ServerSentEvents)]
        public async Task RequestToActiveConnectionId409ForStreamingTransports(HttpTransportType transportType)
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var connection = manager.CreateConnection();
                connection.TransportType = transportType;

                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);

                var context1 = MakeRequest("/foo", connection);
                var context2 = MakeRequest("/foo", connection);

                SetTransport(context1, transportType);
                SetTransport(context2, transportType);

                var services = new ServiceCollection();
                services.AddSingleton<TestConnectionHandler>();
                var builder = new ConnectionBuilder(services.BuildServiceProvider());
                builder.UseConnectionHandler<TestConnectionHandler>();
                var app = builder.Build();
                var options = new HttpConnectionDispatcherOptions();
                var request1 = dispatcher.ExecuteAsync(context1, options, app);

                await dispatcher.ExecuteAsync(context2, options, app);

                Assert.Equal(StatusCodes.Status409Conflict, context2.Response.StatusCode);

                var webSocketTask = Task.CompletedTask;

                var ws = (TestWebSocketConnectionFeature)context1.Features.Get<IHttpWebSocketFeature>();
                if (ws != null)
                {
                    await ws.Client.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                }

                manager.CloseConnections();

                await request1.OrTimeout();
            }
        }

        [Fact]
        public async Task RequestToActiveConnectionIdKillsPreviousConnectionLongPolling()
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var connection = manager.CreateConnection();
                connection.TransportType = HttpTransportType.LongPolling;

                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);

                var context1 = MakeRequest("/foo", connection);
                var context2 = MakeRequest("/foo", connection);

                var services = new ServiceCollection();
                services.AddSingleton<TestConnectionHandler>();
                var builder = new ConnectionBuilder(services.BuildServiceProvider());
                builder.UseConnectionHandler<TestConnectionHandler>();
                var app = builder.Build();
                var options = new HttpConnectionDispatcherOptions();
                var request1 = dispatcher.ExecuteAsync(context1, options, app);
                Assert.True(request1.IsCompleted);

                request1 = dispatcher.ExecuteAsync(context1, options, app);
                var request2 = dispatcher.ExecuteAsync(context2, options, app);

                await request1;

                Assert.Equal(StatusCodes.Status204NoContent, context1.Response.StatusCode);
                Assert.Equal(HttpConnectionStatus.Active, connection.Status);

                Assert.False(request2.IsCompleted);

                manager.CloseConnections();

                await request2;
            }
        }

        [Fact]
        [Flaky("https://github.com/aspnet/AspNetCore-Internal/issues/2040", "All")]
        public async Task MultipleRequestsToActiveConnectionId409ForLongPolling()
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var connection = manager.CreateConnection();
                connection.TransportType = HttpTransportType.LongPolling;

                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);

                var context1 = MakeRequest("/foo", connection);
                var context2 = MakeRequest("/foo", connection);

                var services = new ServiceCollection();
                services.AddSingleton<TestConnectionHandler>();
                var builder = new ConnectionBuilder(services.BuildServiceProvider());
                builder.UseConnectionHandler<TestConnectionHandler>();
                var app = builder.Build();
                var options = new HttpConnectionDispatcherOptions();

                // Prime the polling. Expect any empty response showing the transport is initialized.
                var request1 = dispatcher.ExecuteAsync(context1, options, app);
                Assert.True(request1.IsCompleted);

                // Manually control PreviousPollTask instead of using a real PreviousPollTask, because a real
                // PreviousPollTask might complete too early when the second request cancels it.
                var lastPollTcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                connection.PreviousPollTask = lastPollTcs.Task;

                request1 = dispatcher.ExecuteAsync(context1, options, app);
                var request2 = dispatcher.ExecuteAsync(context2, options, app);

                Assert.False(request1.IsCompleted);
                Assert.False(request2.IsCompleted);

                lastPollTcs.SetResult(null);

                var completedTask = await Task.WhenAny(request1, request2).OrTimeout();

                if (completedTask == request1)
                {
                    Assert.Equal(StatusCodes.Status409Conflict, context1.Response.StatusCode);
                    Assert.False(request2.IsCompleted);
                }
                else
                {
                    Assert.Equal(StatusCodes.Status409Conflict, context2.Response.StatusCode);
                    Assert.False(request1.IsCompleted);
                }

                Assert.Equal(HttpConnectionStatus.Active, connection.Status);

                manager.CloseConnections();

                await request1.OrTimeout();
                await request2.OrTimeout();
            }
        }

        [Theory]
        [InlineData(HttpTransportType.ServerSentEvents)]
        [InlineData(HttpTransportType.LongPolling)]
        public async Task RequestToDisposedConnectionIdReturns404(HttpTransportType transportType)
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var connection = manager.CreateConnection();
                connection.TransportType = transportType;
                connection.Status = HttpConnectionStatus.Disposed;

                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);

                var context = MakeRequest("/foo", connection);
                SetTransport(context, transportType);

                var services = new ServiceCollection();
                services.AddSingleton<TestConnectionHandler>();
                var builder = new ConnectionBuilder(services.BuildServiceProvider());
                builder.UseConnectionHandler<TestConnectionHandler>();
                var app = builder.Build();
                var options = new HttpConnectionDispatcherOptions();
                await dispatcher.ExecuteAsync(context, options, app);


                Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
            }
        }

        [Fact]
        public async Task ConnectionStateSetToInactiveAfterPoll()
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var connection = manager.CreateConnection();
                connection.TransportType = HttpTransportType.LongPolling;

                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);

                var context = MakeRequest("/foo", connection);

                var services = new ServiceCollection();
                services.AddSingleton<TestConnectionHandler>();
                var builder = new ConnectionBuilder(services.BuildServiceProvider());
                builder.UseConnectionHandler<TestConnectionHandler>();
                var app = builder.Build();
                var options = new HttpConnectionDispatcherOptions();
                var task = dispatcher.ExecuteAsync(context, options, app);

                var buffer = Encoding.UTF8.GetBytes("Hello World");

                // Write to the transport so the poll yields
                await connection.Transport.Output.WriteAsync(buffer);

                await task;

                Assert.Equal(HttpConnectionStatus.Inactive, connection.Status);
                Assert.NotNull(connection.GetHttpContext());

                Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            }
        }

        [Fact]
        public async Task BlockingConnectionWorksWithStreamingConnections()
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var connection = manager.CreateConnection();
                connection.TransportType = HttpTransportType.ServerSentEvents;

                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);

                var context = MakeRequest("/foo", connection);
                SetTransport(context, HttpTransportType.ServerSentEvents);

                var services = new ServiceCollection();
                services.AddSingleton<BlockingConnectionHandler>();
                var builder = new ConnectionBuilder(services.BuildServiceProvider());
                builder.UseConnectionHandler<BlockingConnectionHandler>();
                var app = builder.Build();
                var options = new HttpConnectionDispatcherOptions();
                var task = dispatcher.ExecuteAsync(context, options, app);

                var buffer = Encoding.UTF8.GetBytes("Hello World");

                // Write to the application
                await connection.Application.Output.WriteAsync(buffer);

                await task;

                Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
                var exists = manager.TryGetConnection(connection.ConnectionToken, out _);
                Assert.False(exists);
            }
        }

        [Fact]
        public async Task BlockingConnectionWorksWithLongPollingConnection()
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var connection = manager.CreateConnection();
                connection.TransportType = HttpTransportType.LongPolling;

                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);

                var context = MakeRequest("/foo", connection);

                var services = new ServiceCollection();
                services.AddSingleton<BlockingConnectionHandler>();
                var builder = new ConnectionBuilder(services.BuildServiceProvider());
                builder.UseConnectionHandler<BlockingConnectionHandler>();
                var app = builder.Build();
                var options = new HttpConnectionDispatcherOptions();

                // Initial poll
                var task = dispatcher.ExecuteAsync(context, options, app);
                Assert.True(task.IsCompleted);
                Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);

                // Real long running poll
                task = dispatcher.ExecuteAsync(context, options, app);

                var buffer = Encoding.UTF8.GetBytes("Hello World");

                // Write to the application
                await connection.Application.Output.WriteAsync(buffer);

                await task;

                Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
                var exists = manager.TryGetConnection(connection.ConnectionToken, out _);
                Assert.False(exists);
            }
        }

        [Fact]
        public async Task AttemptingToPollWhileAlreadyPollingReplacesTheCurrentPoll()
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var connection = manager.CreateConnection();
                connection.TransportType = HttpTransportType.LongPolling;

                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);

                var services = new ServiceCollection();
                services.AddSingleton<TestConnectionHandler>();
                var builder = new ConnectionBuilder(services.BuildServiceProvider());
                builder.UseConnectionHandler<TestConnectionHandler>();
                var app = builder.Build();
                var options = new HttpConnectionDispatcherOptions();

                var context1 = MakeRequest("/foo", connection);
                // This is the initial poll to make sure things are setup
                var task1 = dispatcher.ExecuteAsync(context1, options, app);
                Assert.True(task1.IsCompleted);
                task1 = dispatcher.ExecuteAsync(context1, options, app);
                var context2 = MakeRequest("/foo", connection);
                var task2 = dispatcher.ExecuteAsync(context2, options, app);

                // Task 1 should finish when request 2 arrives
                await task1.OrTimeout();

                // Send a message from the app to complete Task 2
                await connection.Transport.Output.WriteAsync(Encoding.UTF8.GetBytes("Hello, World"));

                await task2.OrTimeout();

                // Verify the results
                Assert.Equal(StatusCodes.Status204NoContent, context1.Response.StatusCode);
                Assert.Equal(string.Empty, GetContentAsString(context1.Response.Body));
                Assert.Equal(StatusCodes.Status200OK, context2.Response.StatusCode);
                Assert.Equal("Hello, World", GetContentAsString(context2.Response.Body));
            }
        }

        [Theory]
        [InlineData(HttpTransportType.LongPolling, null)]
        [InlineData(HttpTransportType.ServerSentEvents, TransferFormat.Text)]
        [InlineData(HttpTransportType.WebSockets, TransferFormat.Binary | TransferFormat.Text)]
        public async Task TransferModeSet(HttpTransportType transportType, TransferFormat? expectedTransferFormats)
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var connection = manager.CreateConnection();
                connection.TransportType = transportType;

                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);

                var context = MakeRequest("/foo", connection);
                SetTransport(context, transportType);

                var services = new ServiceCollection();
                services.AddSingleton<ImmediatelyCompleteConnectionHandler>();
                var builder = new ConnectionBuilder(services.BuildServiceProvider());
                builder.UseConnectionHandler<ImmediatelyCompleteConnectionHandler>();
                var app = builder.Build();

                var options = new HttpConnectionDispatcherOptions();
                options.WebSockets.CloseTimeout = TimeSpan.FromSeconds(0);
                await dispatcher.ExecuteAsync(context, options, app);

                if (expectedTransferFormats != null)
                {
                    var transferFormatFeature = connection.Features.Get<ITransferFormatFeature>();
                    Assert.Equal(expectedTransferFormats.Value, transferFormatFeature.SupportedFormats);
                }
            }
        }

        [ConditionalFact]
        [OSSkipCondition(OperatingSystems.Linux | OperatingSystems.MacOSX)]
        public async Task LongPollingKeepsWindowsIdentityBetweenRequests()
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var connection = manager.CreateConnection();
                connection.TransportType = HttpTransportType.LongPolling;
                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);
                var context = new DefaultHttpContext();
                var services = new ServiceCollection();
                services.AddOptions();
                services.AddSingleton<TestConnectionHandler>();
                services.AddLogging();
                var sp = services.BuildServiceProvider();
                context.Request.Path = "/foo";
                context.Request.Method = "GET";
                context.RequestServices = sp;
                var values = new Dictionary<string, StringValues>();
                values["id"] = connection.ConnectionToken;
                values["negotiateVersion"] = "1";
                var qs = new QueryCollection(values);
                context.Request.Query = qs;
                var builder = new ConnectionBuilder(sp);
                builder.UseConnectionHandler<TestConnectionHandler>();
                var app = builder.Build();
                var options = new HttpConnectionDispatcherOptions();

                var windowsIdentity = WindowsIdentity.GetAnonymous();
                context.User = new WindowsPrincipal(windowsIdentity);

                // would get stuck if EndPoint was running
                await dispatcher.ExecuteAsync(context, options, app).OrTimeout();

                Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
                var currentUser = connection.User;

                var connectionHandlerTask = dispatcher.ExecuteAsync(context, options, app);
                await connection.Transport.Output.WriteAsync(Encoding.UTF8.GetBytes("Unblock")).AsTask().OrTimeout();
                await connectionHandlerTask.OrTimeout();

                // This is the important check
                Assert.Same(currentUser, connection.User);

                Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            }
        }

        [Fact]
        public async Task SetsInherentKeepAliveFeatureOnFirstLongPollingRequest()
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var connection = manager.CreateConnection();
                connection.TransportType = HttpTransportType.LongPolling;

                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);

                var context = MakeRequest("/foo", connection);

                var services = new ServiceCollection();
                services.AddSingleton<TestConnectionHandler>();
                var builder = new ConnectionBuilder(services.BuildServiceProvider());
                builder.UseConnectionHandler<TestConnectionHandler>();
                var app = builder.Build();
                var options = new HttpConnectionDispatcherOptions();
                options.LongPolling.PollTimeout = TimeSpan.FromMilliseconds(1); // We don't care about the poll itself

                await dispatcher.ExecuteAsync(context, options, app).OrTimeout();

                Assert.True(connection.HasInherentKeepAlive);

                // Check via the feature as well to make sure it's there.
                Assert.True(connection.Features.Get<IConnectionInherentKeepAliveFeature>().HasInherentKeepAlive);
            }
        }

        [Theory]
        [InlineData(HttpTransportType.ServerSentEvents)]
        [InlineData(HttpTransportType.WebSockets)]
        public async Task DeleteEndpointRejectsRequestToTerminateNonLongPollingTransport(HttpTransportType transportType)
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var connection = manager.CreateConnection();
                connection.TransportType = transportType;

                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);

                var context = MakeRequest("/foo", connection);
                SetTransport(context, transportType);

                var serviceCollection = new ServiceCollection();
                serviceCollection.AddSingleton<TestConnectionHandler>();
                var services = serviceCollection.BuildServiceProvider();
                var builder = new ConnectionBuilder(services);
                builder.UseConnectionHandler<TestConnectionHandler>();
                var app = builder.Build();
                var options = new HttpConnectionDispatcherOptions();

                _ = dispatcher.ExecuteAsync(context, options, app).OrTimeout();

                // Issue the delete request
                var deleteContext = new DefaultHttpContext();
                deleteContext.Request.Path = "/foo";
                deleteContext.Request.QueryString = new QueryString($"?id={connection.ConnectionToken}");
                deleteContext.Request.Method = "DELETE";
                var ms = new MemoryStream();
                deleteContext.Response.Body = ms;

                await dispatcher.ExecuteAsync(deleteContext, options, app).OrTimeout();

                // Verify the response from the DELETE request
                Assert.Equal(StatusCodes.Status400BadRequest, deleteContext.Response.StatusCode);
                Assert.Equal("text/plain", deleteContext.Response.ContentType);
                Assert.Equal("Cannot terminate this connection using the DELETE endpoint.", Encoding.UTF8.GetString(ms.ToArray()));
            }
        }

        [Fact]
        public async Task DeleteEndpointGracefullyTerminatesLongPolling()
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var connection = manager.CreateConnection();
                connection.TransportType = HttpTransportType.LongPolling;

                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);

                var context = MakeRequest("/foo", connection);

                var services = new ServiceCollection();
                services.AddSingleton<TestConnectionHandler>();
                var builder = new ConnectionBuilder(services.BuildServiceProvider());
                builder.UseConnectionHandler<TestConnectionHandler>();
                var app = builder.Build();
                var options = new HttpConnectionDispatcherOptions();

                var pollTask = dispatcher.ExecuteAsync(context, options, app);
                Assert.True(pollTask.IsCompleted);

                // Now send the second poll
                pollTask = dispatcher.ExecuteAsync(context, options, app);

                // Issue the delete request and make sure the poll completes
                var deleteContext = new DefaultHttpContext();
                deleteContext.Request.Path = "/foo";
                deleteContext.Request.QueryString = new QueryString($"?id={connection.ConnectionToken}");
                deleteContext.Request.Method = "DELETE";

                Assert.False(pollTask.IsCompleted);

                await dispatcher.ExecuteAsync(deleteContext, options, app).OrTimeout();

                await pollTask.OrTimeout();

                // Verify that everything shuts down
                await connection.ApplicationTask.OrTimeout();
                await connection.TransportTask.OrTimeout();

                // Verify the response from the DELETE request
                Assert.Equal(StatusCodes.Status202Accepted, deleteContext.Response.StatusCode);
                Assert.Equal("text/plain", deleteContext.Response.ContentType);

                // Verify the connection was removed from the manager
                Assert.False(manager.TryGetConnection(connection.ConnectionToken, out _));
            }
        }

        [Fact]
        public async Task DeleteEndpointGracefullyTerminatesLongPollingEvenWhenBetweenPolls()
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var connection = manager.CreateConnection();
                connection.TransportType = HttpTransportType.LongPolling;

                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);

                var context = MakeRequest("/foo", connection);

                var services = new ServiceCollection();
                services.AddSingleton<TestConnectionHandler>();
                var builder = new ConnectionBuilder(services.BuildServiceProvider());
                builder.UseConnectionHandler<TestConnectionHandler>();
                var app = builder.Build();
                var options = new HttpConnectionDispatcherOptions();
                options.LongPolling.PollTimeout = TimeSpan.FromMilliseconds(1);

                await dispatcher.ExecuteAsync(context, options, app).OrTimeout();

                // Issue the delete request and make sure the poll completes
                var deleteContext = new DefaultHttpContext();
                deleteContext.Request.Path = "/foo";
                deleteContext.Request.QueryString = new QueryString($"?id={connection.ConnectionToken}");
                deleteContext.Request.Method = "DELETE";

                await dispatcher.ExecuteAsync(deleteContext, options, app).OrTimeout();

                // Verify the response from the DELETE request
                Assert.Equal(StatusCodes.Status202Accepted, deleteContext.Response.StatusCode);
                Assert.Equal("text/plain", deleteContext.Response.ContentType);

                // Verify that everything shuts down
                await connection.ApplicationTask.OrTimeout();
                await connection.TransportTask.OrTimeout();

                Assert.NotNull(connection.DisposeAndRemoveTask);

                await connection.DisposeAndRemoveTask.OrTimeout();

                // Verify the connection was removed from the manager
                Assert.False(manager.TryGetConnection(connection.ConnectionToken, out _));
            }
        }

        [Fact]
        public async Task NegotiateDoesNotReturnWebSocketsWhenNotAvailable()
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);
                var context = new DefaultHttpContext();
                context.Features.Set<IHttpResponseFeature>(new ResponseFeature());
                var services = new ServiceCollection();
                services.AddSingleton<TestConnectionHandler>();
                services.AddOptions();
                var ms = new MemoryStream();
                context.Request.Path = "/foo";
                context.Request.Method = "POST";
                context.Response.Body = ms;
                context.Request.QueryString = new QueryString("?negotiateVersion=1");
                await dispatcher.ExecuteNegotiateAsync(context, new HttpConnectionDispatcherOptions { Transports = HttpTransportType.WebSockets });

                var negotiateResponse = JsonConvert.DeserializeObject<JObject>(Encoding.UTF8.GetString(ms.ToArray()));
                var availableTransports = (JArray)negotiateResponse["availableTransports"];

                Assert.Empty(availableTransports);
            }
        }

        private class ControllableMemoryStream : MemoryStream
        {
            private readonly SyncPoint _syncPoint;

            public ControllableMemoryStream(SyncPoint syncPoint)
            {
                _syncPoint = syncPoint;
            }

            public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
            {
                await _syncPoint.WaitToContinue();

                await base.CopyToAsync(destination, bufferSize, cancellationToken);
            }
        }

        [Fact]
        public async Task WriteThatIsDisposedBeforeCompleteReturns404()
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var pipeOptions = new PipeOptions(pauseWriterThreshold: 13, resumeWriterThreshold: 10);
                var connection = manager.CreateConnection(pipeOptions, pipeOptions);
                connection.TransportType = HttpTransportType.LongPolling;

                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);

                var services = new ServiceCollection();
                services.AddSingleton<TestConnectionHandler>();
                var builder = new ConnectionBuilder(services.BuildServiceProvider());
                builder.UseConnectionHandler<TestConnectionHandler>();
                var app = builder.Build();
                var options = new HttpConnectionDispatcherOptions();

                SyncPoint streamCopySyncPoint = new SyncPoint();

                using (var responseBody = new MemoryStream())
                using (var requestBody = new ControllableMemoryStream(streamCopySyncPoint))
                {
                    var context = new DefaultHttpContext();
                    context.Request.Body = requestBody;
                    context.Response.Body = responseBody;
                    context.Request.Path = "/foo";
                    context.Request.Method = "POST";
                    var values = new Dictionary<string, StringValues>();
                    values["id"] = connection.ConnectionToken;
                    values["negotiateVersion"] = "1";
                    var qs = new QueryCollection(values);
                    context.Request.Query = qs;
                    var buffer = Encoding.UTF8.GetBytes("Hello, world");
                    requestBody.Write(buffer, 0, buffer.Length);
                    requestBody.Seek(0, SeekOrigin.Begin);

                    // Write
                    var sendTask = dispatcher.ExecuteAsync(context, options, app);

                    // Wait on the sync point inside ApplicationStream.CopyToAsync
                    await streamCopySyncPoint.WaitForSyncPoint();

                    // Start disposing. This will close the output and cause the write to error
                    var disposeTask = connection.DisposeAsync().OrTimeout();

                    // Continue writing on a completed writer
                    streamCopySyncPoint.Continue();

                    await sendTask.OrTimeout();
                    await disposeTask.OrTimeout();

                    // Ensure response status is correctly set
                    Assert.Equal(404, context.Response.StatusCode);
                }
            }
        }

        [Fact]
        public async Task CanDisposeWhileWriteLockIsBlockedOnBackpressureAndResponseReturns404()
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var pipeOptions = new PipeOptions(pauseWriterThreshold: 13, resumeWriterThreshold: 10);
                var connection = manager.CreateConnection(pipeOptions, pipeOptions);
                connection.TransportType = HttpTransportType.LongPolling;

                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);

                var services = new ServiceCollection();
                services.AddSingleton<TestConnectionHandler>();
                var builder = new ConnectionBuilder(services.BuildServiceProvider());
                builder.UseConnectionHandler<TestConnectionHandler>();
                var app = builder.Build();
                var options = new HttpConnectionDispatcherOptions();

                using (var responseBody = new MemoryStream())
                using (var requestBody = new MemoryStream())
                {
                    var context = new DefaultHttpContext();
                    context.Request.Body = requestBody;
                    context.Response.Body = responseBody;
                    context.Request.Path = "/foo";
                    context.Request.Method = "POST";
                    var values = new Dictionary<string, StringValues>();
                    values["id"] = connection.ConnectionToken;
                    values["negotiateVersion"] = "1";
                    var qs = new QueryCollection(values);
                    context.Request.Query = qs;
                    var buffer = Encoding.UTF8.GetBytes("Hello, world");
                    requestBody.Write(buffer, 0, buffer.Length);
                    requestBody.Seek(0, SeekOrigin.Begin);

                    // Write some data to the pipe to fill it up and make the next write wait
                    await connection.ApplicationStream.WriteAsync(buffer, 0, buffer.Length).OrTimeout();

                    // Write. This will take the WriteLock and block because of back pressure
                    var sendTask = dispatcher.ExecuteAsync(context, options, app);

                    // Start disposing. This will take the StateLock and attempt to take the WriteLock
                    // Dispose will cancel pending flush and should unblock WriteLock
                    await connection.DisposeAsync().OrTimeout();

                    // Sends were unblocked
                    await sendTask.OrTimeout();

                    Assert.Equal(404, context.Response.StatusCode);
                }
            }
        }

        [Fact]
        public async Task LongPollingCanPollIfWritePipeHasBackpressure()
        {
            using (StartVerifiableLog())
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var pipeOptions = new PipeOptions(pauseWriterThreshold: 13, resumeWriterThreshold: 10);
                var connection = manager.CreateConnection(pipeOptions, pipeOptions);
                connection.TransportType = HttpTransportType.LongPolling;

                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);

                var services = new ServiceCollection();
                services.AddSingleton<TestConnectionHandler>();
                var builder = new ConnectionBuilder(services.BuildServiceProvider());
                builder.UseConnectionHandler<TestConnectionHandler>();
                var app = builder.Build();
                var options = new HttpConnectionDispatcherOptions();

                using (var responseBody = new MemoryStream())
                using (var requestBody = new MemoryStream())
                {
                    var context = new DefaultHttpContext();
                    context.Request.Body = requestBody;
                    context.Response.Body = responseBody;
                    context.Request.Path = "/foo";
                    context.Request.Method = "POST";
                    var values = new Dictionary<string, StringValues>();
                    values["id"] = connection.ConnectionToken;
                    values["negotiateVersion"] = "1";
                    var qs = new QueryCollection(values);
                    context.Request.Query = qs;
                    var buffer = Encoding.UTF8.GetBytes("Hello, world");
                    requestBody.Write(buffer, 0, buffer.Length);
                    requestBody.Seek(0, SeekOrigin.Begin);

                    // Write some data to the pipe to fill it up and make the next write wait
                    await connection.ApplicationStream.WriteAsync(buffer, 0, buffer.Length).OrTimeout();

                    // This will block until the pipe is unblocked
                    var sendTask = dispatcher.ExecuteAsync(context, options, app).OrTimeout();
                    Assert.False(sendTask.IsCompleted);

                    var pollContext = MakeRequest("/foo", connection);
                    // This should unblock the send that is waiting because of backpressure
                    // Testing deadlock regression where pipe backpressure would hold the same lock that poll would use
                    await dispatcher.ExecuteAsync(pollContext, options, app).OrTimeout();

                    await sendTask.OrTimeout();
                }
            }
        }

        [Fact]
        public async Task ErrorDuringPollWillCloseConnection()
        {
            bool ExpectedErrors(WriteContext writeContext)
            {
                return (writeContext.LoggerName.Equals("Microsoft.AspNetCore.Http.Connections.Internal.Transports.LongPollingTransport") &&
                       writeContext.EventId.Name == "LongPollingTerminated") ||
                       (writeContext.LoggerName == typeof(HttpConnectionManager).FullName &&
                       writeContext.EventId.Name == "FailedDispose");
            }

            using (StartVerifiableLog(expectedErrorsFilter: ExpectedErrors))
            {
                var manager = CreateConnectionManager(LoggerFactory);
                var connection = manager.CreateConnection();
                connection.TransportType = HttpTransportType.LongPolling;

                var dispatcher = new HttpConnectionDispatcher(manager, LoggerFactory);

                var services = new ServiceCollection();
                services.AddSingleton<TestConnectionHandler>();
                var builder = new ConnectionBuilder(services.BuildServiceProvider());
                builder.UseConnectionHandler<TestConnectionHandler>();
                var app = builder.Build();
                var options = new HttpConnectionDispatcherOptions();

                var context = MakeRequest("/foo", connection);

                // Initial poll will complete immediately
                await dispatcher.ExecuteAsync(context, options, app).OrTimeout();

                var pollContext = MakeRequest("/foo", connection);
                var pollTask = dispatcher.ExecuteAsync(pollContext, options, app);
                // fail LongPollingTransport ReadAsync
                connection.Transport.Output.Complete(new InvalidOperationException());
                await pollTask.OrTimeout();

                Assert.Equal(StatusCodes.Status500InternalServerError, pollContext.Response.StatusCode);
                Assert.False(manager.TryGetConnection(connection.ConnectionToken, out var _));
            }
        }

        private static async Task CheckTransportSupported(HttpTransportType supportedTransports, HttpTransportType transportType, int status, ILoggerFactory loggerFactory)
        {
            var manager = CreateConnectionManager(loggerFactory);
            var connection = manager.CreateConnection();
            connection.TransportType = transportType;

            var dispatcher = new HttpConnectionDispatcher(manager, loggerFactory);
            using (var strm = new MemoryStream())
            {
                var context = new DefaultHttpContext();
                context.Features.Set<IHttpResponseFeature>(new ResponseFeature());
                context.Response.Body = strm;
                var services = new ServiceCollection();
                services.AddOptions();
                services.AddSingleton<ImmediatelyCompleteConnectionHandler>();
                SetTransport(context, transportType);
                context.Request.Path = "/foo";
                context.Request.Method = "GET";
                var values = new Dictionary<string, StringValues>();
                values["id"] = connection.ConnectionToken;
                values["negotiateVersion"] = "1";
                var qs = new QueryCollection(values);
                context.Request.Query = qs;

                var builder = new ConnectionBuilder(services.BuildServiceProvider());
                builder.UseConnectionHandler<ImmediatelyCompleteConnectionHandler>();
                var app = builder.Build();
                var options = new HttpConnectionDispatcherOptions();
                options.Transports = supportedTransports;

                await dispatcher.ExecuteAsync(context, options, app);
                Assert.Equal(status, context.Response.StatusCode);
                await strm.FlushAsync();

                // Check the message for 404
                if (status == 404)
                {
                    Assert.Equal($"{transportType} transport not supported by this end point type", Encoding.UTF8.GetString(strm.ToArray()));
                }
            }
        }

        private static DefaultHttpContext MakeRequest(string path, HttpConnectionContext connection, string format = null)
        {
            var context = new DefaultHttpContext();
            context.Features.Set<IHttpResponseFeature>(new ResponseFeature());
            context.Request.Path = path;
            context.Request.Method = "GET";
            var values = new Dictionary<string, StringValues>();
            values["id"] = connection.ConnectionToken;
            values["negotiateVersion"] = "1";
            if (format != null)
            {
                values["format"] = format;
            }
            var qs = new QueryCollection(values);
            context.Request.Query = qs;
            context.Response.Body = new MemoryStream();
            return context;
        }

        private static void SetTransport(HttpContext context, HttpTransportType transportType)
        {
            switch (transportType)
            {
                case HttpTransportType.WebSockets:
                    context.Features.Set<IHttpWebSocketFeature>(new TestWebSocketConnectionFeature());
                    break;
                case HttpTransportType.ServerSentEvents:
                    context.Request.Headers["Accept"] = "text/event-stream";
                    break;
                default:
                    break;
            }
        }

        private static HttpConnectionManager CreateConnectionManager(ILoggerFactory loggerFactory)
        {
            return new HttpConnectionManager(loggerFactory ?? new LoggerFactory(), new EmptyApplicationLifetime());
        }

        private static HttpConnectionManager CreateConnectionManager(ILoggerFactory loggerFactory, TimeSpan disconnectTimeout)
        {
            var connectionOptions = new ConnectionOptions();
            connectionOptions.DisconnectTimeout = disconnectTimeout;
            return new HttpConnectionManager(loggerFactory ?? new LoggerFactory(), new EmptyApplicationLifetime(), Options.Create(connectionOptions));
        }

        private string GetContentAsString(Stream body)
        {
            Assert.True(body.CanSeek, "Can't get content of a non-seekable stream");
            body.Seek(0, SeekOrigin.Begin);
            using (var reader = new StreamReader(body))
            {
                return reader.ReadToEnd();
            }
        }
    }

    public class NeverEndingConnectionHandler : ConnectionHandler
    {
        public override Task OnConnectedAsync(ConnectionContext connection)
        {
            var tcs = new TaskCompletionSource<object>();
            return tcs.Task;
        }
    }

    public class BlockingConnectionHandler : ConnectionHandler
    {
        public override Task OnConnectedAsync(ConnectionContext connection)
        {
            var result = connection.Transport.Input.ReadAsync().AsTask().Result;
            connection.Transport.Input.AdvanceTo(result.Buffer.End);
            return Task.CompletedTask;
        }
    }

    public class SynchronusExceptionConnectionHandler : ConnectionHandler
    {
        public override Task OnConnectedAsync(ConnectionContext connection)
        {
            throw new InvalidOperationException();
        }
    }

    public class ImmediatelyCompleteConnectionHandler : ConnectionHandler
    {
        public override Task OnConnectedAsync(ConnectionContext connection)
        {
            return Task.CompletedTask;
        }
    }

    public class HttpContextConnectionHandler : ConnectionHandler
    {
        public override async Task OnConnectedAsync(ConnectionContext connection)
        {
            while (true)
            {
                var result = await connection.Transport.Input.ReadAsync();

                try
                {
                    if (result.IsCompleted)
                    {
                        break;
                    }

                    // Make sure we have an http context
                    var context = connection.GetHttpContext();
                    Assert.NotNull(context);

                    // Setting the response headers should have no effect
                    context.Response.ContentType = "application/xml";

                    // Echo the results
                    await connection.Transport.Output.WriteAsync(result.Buffer.ToArray());
                }
                finally
                {
                    connection.Transport.Input.AdvanceTo(result.Buffer.End);
                }
            }
        }
    }

    public class TestConnectionHandler : ConnectionHandler
    {
        private TaskCompletionSource<object> _startedTcs = new TaskCompletionSource<object>();

        public Task Started => _startedTcs.Task;

        public override async Task OnConnectedAsync(ConnectionContext connection)
        {
            _startedTcs.TrySetResult(null);

            while (true)
            {
                var result = await connection.Transport.Input.ReadAsync();

                try
                {
                    if (result.IsCompleted)
                    {
                        break;
                    }
                }
                finally
                {
                    connection.Transport.Input.AdvanceTo(result.Buffer.End);
                }
            }
        }
    }

    public class ResponseFeature : HttpResponseFeature
    {
        public override void OnCompleted(Func<object, Task> callback, object state)
        {
        }

        public override void OnStarting(Func<object, Task> callback, object state)
        {
        }
    }
}
