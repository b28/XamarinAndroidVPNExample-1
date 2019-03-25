using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Android.Util;
using Java.IO;
using Java.Lang;
using Java.Nio;
using Java.Nio.Channels;
using Java.Util.Concurrent;

namespace XamarinAndroidVPNExample.VPNService
{
    public class UDPInput : Java.Lang.Object, Java.Lang.IRunnable
    {
        private const string TAG = "UDPInput";
        private const int HEADER_SIZE = Packet.IP4_HEADER_SIZE + Packet.UDP_HEADER_SIZE;

        private Selector selector;
        private ConcurrentLinkedQueue outputQueue;

        public UDPInput(ConcurrentLinkedQueue outputQueue, Selector selector)
        {
            this.outputQueue = outputQueue;
            this.selector = selector;
        }

        public void Run()
        {
            try
            {
                Log.Info(TAG, "Started");
                while (!Thread.Interrupted())
                {
                    int readyChannels = selector.Select();

                    if (readyChannels == 0)
                    {
                        Thread.Sleep(10);
                        continue;
                    }

                    System.Console.WriteLine("UDP In");

                    var keys = selector.SelectedKeys().ToList();

                    foreach(var key in keys)
                    {
                        if (!Thread.Interrupted())
                        {
                            if (key.IsValid && key.IsReadable)
                            {
                                selector.SelectedKeys().Remove(key);

                                ByteBuffer receiveBuffer = ByteBufferPool.acquire();
                                // Leave space for the header
                                receiveBuffer.Position(HEADER_SIZE);

                                DatagramChannel inputChannel = (DatagramChannel)key.Channel();
                                // XXX: We should handle any IOExceptions here immediately,
                                // but that probably won't happen with UDP
                                int readBytes = inputChannel.Read(receiveBuffer);

                                Packet referencePacket = (Packet)key.Attachment();
                                referencePacket.updateUDPBuffer(receiveBuffer, readBytes);
                                receiveBuffer.Position(HEADER_SIZE + readBytes);

                                outputQueue.Offer(receiveBuffer);
                            }
                        }
                    }
                }
            }
            catch (InterruptedException e)
            {
                Log.Info(TAG, "Stopping");
            }
            catch (IOException e)
            {
                Log.Warn(TAG, e.ToString(), e);
            }
        }
    }
}
