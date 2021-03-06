﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace DotNetty.Transport.Channels.Sockets
{
    using System;
    using System.Diagnostics;
    using System.Net.Sockets;

    partial class TcpServerSocketChannel<TServerChannel, TChannelFactory>
    {
        public sealed class TcpServerSocketChannelUnsafe : AbstractSocketUnsafe
        {
            public TcpServerSocketChannelUnsafe() //TcpServerSocketChannel channel)
                : base() //channel)
            {
            }

            //new TcpServerSocketChannel Channel => (TcpServerSocketChannel)this.channel;

            public override void FinishRead(SocketChannelAsyncOperation<TServerChannel, TcpServerSocketChannelUnsafe> operation)
            {
                Debug.Assert(_channel.EventLoop.InEventLoop);

                var ch = _channel;
                if (0u >= (uint)(ch.ResetState(StateFlags.ReadScheduled) & StateFlags.Active))
                {
                    return; // read was signaled as a result of channel closure
                }
                IChannelConfiguration config = ch.Configuration;
                IChannelPipeline pipeline = ch.Pipeline;
                IRecvByteBufAllocatorHandle allocHandle = ch.Unsafe.RecvBufAllocHandle;
                allocHandle.Reset(config);

                var closed = false;
                var aborted = false;
                Exception exception = null;

                try
                {
                    Socket connectedSocket = null;
                    try
                    {
                        connectedSocket = operation.AcceptSocket;
                        operation.AcceptSocket = null;
                        operation.Validate();

                        var message = PrepareChannel(connectedSocket);

                        connectedSocket = null;
                        ch.ReadPending = false;
                        _ = pipeline.FireChannelRead(message);
                        allocHandle.IncMessagesRead(1);

                        if (!config.AutoRead && !ch.ReadPending)
                        {
                            // ChannelConfig.setAutoRead(false) was called in the meantime.
                            // Completed Accept has to be processed though.
                            return;
                        }

                        while (allocHandle.ContinueReading())
                        {
                            connectedSocket = ch.Socket.Accept();
                            message = PrepareChannel(connectedSocket);

                            connectedSocket = null;
                            ch.ReadPending = false;
                            _ = pipeline.FireChannelRead(message);
                            allocHandle.IncMessagesRead(1);
                        }
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode.IsSocketAbortError())
                    {
                        ch.Socket.SafeClose(); // Unbind......
                        exception = ex;
                        aborted = true;
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock)
                    {
                    }
                    catch (SocketException ex)
                    {
                        // socket exceptions here are internal to channel's operation and should not go through the pipeline
                        // especially as they have no effect on overall channel's operation
                        if (Logger.InfoEnabled) Logger.ExceptionOnAccept(ex);
                    }
                    catch (ObjectDisposedException)
                    {
                        closed = true;
                    }
                    catch (Exception ex)
                    {
                        exception = ex;
                    }

                    allocHandle.ReadComplete();
                    _ = pipeline.FireChannelReadComplete();

                    if (exception is object)
                    {
                        // ServerChannel should not be closed even on SocketException because it can often continue
                        // accepting incoming connections. (e.g. too many open files)

                        _ = pipeline.FireExceptionCaught(exception);
                    }

                    if (ch.Open)
                    {
                        if (closed) { Close(VoidPromise()); }
                        else if (aborted) { ch.CloseSafe(); }
                    }
                }
                finally
                {
                    // Check if there is a readPending which was not processed yet.
                    if (!closed && (ch.ReadPending || config.AutoRead))
                    {
                        ch.DoBeginRead();
                    }
                }
            }

            ISocketChannel PrepareChannel(Socket socket)
            {
                try
                {
                    return _channel._channelFactory.CreateChannel(_channel, socket); // ## 苦竹 修改 ## return new TcpSocketChannel(this.channel, socket, true);
                }
                catch (Exception ex)
                {
                    var warnEnabled = Logger.WarnEnabled;
                    if (warnEnabled) Logger.FailedToCreateANewChannelFromAcceptedSocket(ex);
                    try
                    {
                        socket.Dispose();
                    }
                    catch (Exception ex2)
                    {
                        if (warnEnabled) Logger.FailedToCloseASocketCleanly(ex2);
                    }
                    throw;
                }
            }
        }
    }
}