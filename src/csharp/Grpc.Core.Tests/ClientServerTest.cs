#region Copyright notice and license

// Copyright 2015, Google Inc.
// All rights reserved.
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are
// met:
//
//     * Redistributions of source code must retain the above copyright
// notice, this list of conditions and the following disclaimer.
//     * Redistributions in binary form must reproduce the above
// copyright notice, this list of conditions and the following disclaimer
// in the documentation and/or other materials provided with the
// distribution.
//     * Neither the name of Google Inc. nor the names of its
// contributors may be used to endorse or promote products derived from
// this software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
// A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
// OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
// SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
// LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
// THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

#endregion

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Core.Internal;
using Grpc.Core.Utils;
using NUnit.Framework;

namespace Grpc.Core.Tests
{
    public class ClientServerTest
    {
        const string Host = "127.0.0.1";
        const string ServiceName = "/tests.Test";

        static readonly Method<string, string> EchoMethod = new Method<string, string>(
            MethodType.Unary,
            "/tests.Test/Echo",
            Marshallers.StringMarshaller,
            Marshallers.StringMarshaller);

        static readonly Method<string, string> ConcatAndEchoMethod = new Method<string, string>(
            MethodType.ClientStreaming,
            "/tests.Test/ConcatAndEcho",
            Marshallers.StringMarshaller,
            Marshallers.StringMarshaller);

        static readonly Method<string, string> NonexistentMethod = new Method<string, string>(
            MethodType.Unary,
            "/tests.Test/NonexistentMethod",
            Marshallers.StringMarshaller,
            Marshallers.StringMarshaller);

        static readonly ServerServiceDefinition ServiceDefinition = ServerServiceDefinition.CreateBuilder(ServiceName)
            .AddMethod(EchoMethod, EchoHandler)
            .AddMethod(ConcatAndEchoMethod, ConcatAndEchoHandler)
            .Build();

        Server server;
        Channel channel;

        [SetUp]
        public void Init()
        {
            server = new Server
            {
                Services = { ServiceDefinition },
                Ports = { { Host, ServerPort.PickUnused, ServerCredentials.Insecure } }
            };
            server.Start();
            channel = new Channel(Host, server.Ports.Single().BoundPort, Credentials.Insecure);
        }

        [TearDown]
        public void Cleanup()
        {
            channel.Dispose();
            server.ShutdownAsync().Wait();
        }

        [TestFixtureTearDown]
        public void CleanupClass()
        {
            GrpcEnvironment.Shutdown();
        }

        [Test]
        public void UnaryCall()
        {
            var internalCall = new Call<string, string>(ServiceName, EchoMethod, channel, new CallContext());
            Assert.AreEqual("ABC", Calls.BlockingUnaryCall(internalCall, "ABC"));
        }

        [Test]
        public void UnaryCall_ServerHandlerThrows()
        {
            var internalCall = new Call<string, string>(ServiceName, EchoMethod, channel, new CallContext());
            try
            {
                Calls.BlockingUnaryCall(internalCall, "THROW");
                Assert.Fail();
            }
            catch (RpcException e)
            {
                Assert.AreEqual(StatusCode.Unknown, e.Status.StatusCode); 
            }
        }

        [Test]
        public void UnaryCall_ServerHandlerThrowsRpcException()
        {
            var internalCall = new Call<string, string>(ServiceName, EchoMethod, channel, new CallContext());
            try
            {
                Calls.BlockingUnaryCall(internalCall, "THROW_UNAUTHENTICATED");
                Assert.Fail();
            }
            catch (RpcException e)
            {
                Assert.AreEqual(StatusCode.Unauthenticated, e.Status.StatusCode);
            }
        }

        [Test]
        public void UnaryCall_ServerHandlerSetsStatus()
        {
            var internalCall = new Call<string, string>(ServiceName, EchoMethod, channel, new CallContext());
            try
            {
                Calls.BlockingUnaryCall(internalCall, "SET_UNAUTHENTICATED");
                Assert.Fail();
            }
            catch (RpcException e)
            {
                Assert.AreEqual(StatusCode.Unauthenticated, e.Status.StatusCode); 
            }
        }

        [Test]
        public async Task AsyncUnaryCall()
        {
            var internalCall = new Call<string, string>(ServiceName, EchoMethod, channel, new CallContext());
            var result = await Calls.AsyncUnaryCall(internalCall, "ABC");
            Assert.AreEqual("ABC", result);
        }

        [Test]
        public async Task AsyncUnaryCall_ServerHandlerThrows()
        {
            var internalCall = new Call<string, string>(ServiceName, EchoMethod, channel, new CallContext());
            try
            {
                await Calls.AsyncUnaryCall(internalCall, "THROW");
                Assert.Fail();
            }
            catch (RpcException e)
            {
                Assert.AreEqual(StatusCode.Unknown, e.Status.StatusCode);
            }
        }

        [Test]
        public async Task ClientStreamingCall()
        {
            var internalCall = new Call<string, string>(ServiceName, ConcatAndEchoMethod, channel, new CallContext());
            var call = Calls.AsyncClientStreamingCall(internalCall);

            await call.RequestStream.WriteAll(new string[] { "A", "B", "C" });
            Assert.AreEqual("ABC", await call.ResponseAsync);
        }

        [Test]
        public async Task ClientStreamingCall_CancelAfterBegin()
        {
            var cts = new CancellationTokenSource();
            var internalCall = new Call<string, string>(ServiceName, ConcatAndEchoMethod, channel, new CallContext(cancellationToken: cts.Token));
            var call = Calls.AsyncClientStreamingCall(internalCall);

            // TODO(jtattermusch): we need this to ensure call has been initiated once we cancel it.
            await Task.Delay(1000);
            cts.Cancel();

            try
            {
                await call.ResponseAsync;
            }
            catch (RpcException e)
            {
                Assert.AreEqual(StatusCode.Cancelled, e.Status.StatusCode);
            }
        }

        [Test]
        public void AsyncUnaryCall_EchoMetadata()
        {
            var headers = new Metadata
            {
                new Metadata.Entry("ascii-header", "abcdefg"),
                new Metadata.Entry("binary-header-bin", new byte[] { 1, 2, 3, 0, 0xff }),
            };
            var internalCall = new Call<string, string>(ServiceName, EchoMethod, channel, new CallContext(headers: headers));
            var call = Calls.AsyncUnaryCall(internalCall, "ABC");

            Assert.AreEqual("ABC", call.ResponseAsync.Result);

            Assert.AreEqual(StatusCode.OK, call.GetStatus().StatusCode);

            var trailers = call.GetTrailers();
            Assert.AreEqual(2, trailers.Count);
            Assert.AreEqual(headers[0].Key, trailers[0].Key);
            Assert.AreEqual(headers[0].Value, trailers[0].Value);

            Assert.AreEqual(headers[1].Key, trailers[1].Key);
            CollectionAssert.AreEqual(headers[1].ValueBytes, trailers[1].ValueBytes);
        }

        [Test]
        public void UnaryCall_DisposedChannel()
        {
            channel.Dispose();

            var internalCall = new Call<string, string>(ServiceName, EchoMethod, channel, new CallContext());
            Assert.Throws(typeof(ObjectDisposedException), () => Calls.BlockingUnaryCall(internalCall, "ABC"));
        }

        [Test]
        public void UnaryCallPerformance()
        {
            var internalCall = new Call<string, string>(ServiceName, EchoMethod, channel, new CallContext());
            BenchmarkUtil.RunBenchmark(100, 100,
                                       () => { Calls.BlockingUnaryCall(internalCall, "ABC"); });
        }
            
        [Test]
        public void UnknownMethodHandler()
        {
            var internalCall = new Call<string, string>(ServiceName, NonexistentMethod, channel, new CallContext());
            try
            {
                Calls.BlockingUnaryCall(internalCall, "ABC");
                Assert.Fail();
            }
            catch (RpcException e)
            {
                Assert.AreEqual(StatusCode.Unimplemented, e.Status.StatusCode);
            }
        }

        [Test]
        public void UserAgentStringPresent()
        {
            var internalCall = new Call<string, string>(ServiceName, EchoMethod, channel, new CallContext());
            string userAgent = Calls.BlockingUnaryCall(internalCall, "RETURN-USER-AGENT");
            Assert.IsTrue(userAgent.StartsWith("grpc-csharp/"));
        }

        [Test]
        public void PeerInfoPresent()
        {
            var internalCall = new Call<string, string>(ServiceName, EchoMethod, channel, new CallContext());
            string peer = Calls.BlockingUnaryCall(internalCall, "RETURN-PEER");
            Assert.IsTrue(peer.Contains(Host));
        }

        [Test]
        public async Task Channel_WaitForStateChangedAsync()
        {
            Assert.Throws(typeof(TaskCanceledException), 
                async () => await channel.WaitForStateChangedAsync(channel.State, DateTime.UtcNow.AddMilliseconds(10)));

            var stateChangedTask = channel.WaitForStateChangedAsync(channel.State);

            var internalCall = new Call<string, string>(ServiceName, EchoMethod, channel, new CallContext());
            await Calls.AsyncUnaryCall(internalCall, "abc");

            await stateChangedTask;
            Assert.AreEqual(ChannelState.Ready, channel.State);
        }

        [Test]
        public async Task Channel_ConnectAsync()
        {
            await channel.ConnectAsync();
            Assert.AreEqual(ChannelState.Ready, channel.State);
            await channel.ConnectAsync(DateTime.UtcNow.AddMilliseconds(1000));
            Assert.AreEqual(ChannelState.Ready, channel.State);
        }

        private static async Task<string> EchoHandler(string request, ServerCallContext context)
        {
            foreach (Metadata.Entry metadataEntry in context.RequestHeaders)
            {
                if (metadataEntry.Key != "user-agent")
                {
                    context.ResponseTrailers.Add(metadataEntry);
                }
            }

            if (request == "RETURN-USER-AGENT")
            {
                return context.RequestHeaders.Where(entry => entry.Key == "user-agent").Single().Value;
            }

            if (request == "RETURN-PEER")
            {
                return context.Peer;
            }

            if (request == "THROW")
            {
                throw new Exception("This was thrown on purpose by a test");
            }

            if (request == "THROW_UNAUTHENTICATED")
            {
                throw new RpcException(new Status(StatusCode.Unauthenticated, ""));
            }

            if (request == "SET_UNAUTHENTICATED")
            {
                context.Status = new Status(StatusCode.Unauthenticated, "");
            }

            return request;
        }

        private static async Task<string> ConcatAndEchoHandler(IAsyncStreamReader<string> requestStream, ServerCallContext context)
        {
            string result = "";
            await requestStream.ForEach(async (request) =>
            {
                if (request == "THROW")
                {
                    throw new Exception("This was thrown on purpose by a test");
                }
                result += request;
            });
            // simulate processing takes some time.
            await Task.Delay(250);
            return result;
        }
    }
}
