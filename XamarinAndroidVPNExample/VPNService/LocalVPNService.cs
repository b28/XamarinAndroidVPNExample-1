using Android.App;
using Android.Content;
using Android.Net;
using Android.OS;
using Android.Runtime;
using Android.Support.V4.Content;
using Android.Util;
using Java.IO;
using Java.Lang;
using Java.Nio;
using Java.Nio.Channels;
using Java.Util.Concurrent;

namespace XamarinAndroidVPNExample.VPNService
{
    [Service(Label = "LocalVPNService", Name = "xamarinandroidvpnexample.vpnservice.LocalVPNService", Enabled = true, Permission = "android.permission.BIND_VPN_SERVICE")]
    public class LocalVPNService : VpnService
    {
        private const string TAG = "LocalVPNService";
        private const string VPN_ADDRESS = "10.0.0.2"; // Only IPv4 support for now
        private const string VPN_ROUTE = "0.0.0.0"; // Intercept everything
        private const string DNS_ADDRESS = "8.8.8.8"; // Google DNS

        public const string BROADCAST_VPN_STATE = "xamarinandroidvpnexample.vpnservice.VPN_STATE";

        private ParcelFileDescriptor vpnInterface = null;

        private PendingIntent pendingIntent;

        private ConcurrentLinkedQueue deviceToNetworkUDPQueue;
        private ConcurrentLinkedQueue deviceToNetworkTCPQueue;
        private ConcurrentLinkedQueue networkToDeviceQueue;
        private IExecutorService executorService;

        private Selector udpSelector;
        private Selector tcpSelector;

        public static bool isRunning;

        public override void OnCreate()
        {
            base.OnCreate();

            isRunning = true;
            SetupVPN();

            if (vpnInterface == null)
            {
                Log.Error(TAG, "Vpn Interface is null");
                StopSelf();
                return;
            }

            try
            {
                udpSelector = Selector.Open();
                tcpSelector = Selector.Open();
                deviceToNetworkUDPQueue = new ConcurrentLinkedQueue();
                deviceToNetworkTCPQueue = new ConcurrentLinkedQueue();
                networkToDeviceQueue = new ConcurrentLinkedQueue();

                executorService = Executors.NewFixedThreadPool(5);
                executorService.Submit(new UDPInput(networkToDeviceQueue, udpSelector));
                executorService.Submit(new UDPOutput(deviceToNetworkUDPQueue, udpSelector, this));
                executorService.Submit(new TCPInput(networkToDeviceQueue, tcpSelector));
                executorService.Submit(new TCPOutput(deviceToNetworkTCPQueue, networkToDeviceQueue, tcpSelector, this));
                executorService.Submit(new VPNRunnable(vpnInterface.FileDescriptor, deviceToNetworkUDPQueue, deviceToNetworkTCPQueue, networkToDeviceQueue));
                LocalBroadcastManager.GetInstance(this).SendBroadcast(new Intent(BROADCAST_VPN_STATE).PutExtra("running", true));
                Log.Info(TAG, "Started");
            }
            catch (IOException e)
            {
                // TODO: Here and elsewhere, we should explicitly notify the user of any errors
                // and suggest that they stop the service, since we can't do it ourselves
                Log.Error(TAG, "Error starting service", e);
                Cleanup();
            }
        }

        private void SetupVPN()
        {
            if (vpnInterface == null)
            {
                Builder builder = new Builder(this);
                builder.AddAddress(VPN_ADDRESS, 32);
                builder.AddRoute(VPN_ROUTE, 0);
                builder.AddDnsServer(DNS_ADDRESS);
                vpnInterface = builder.SetSession(GetString(Resource.String.app_name)).SetConfigureIntent(pendingIntent).Establish();
            }
        }

        public static bool IsRunning { get { return isRunning; } }

        [return: GeneratedEnum]
        public override StartCommandResult OnStartCommand(Intent intent, [GeneratedEnum] StartCommandFlags flags, int startId)
        {
            return StartCommandResult.Sticky;
        }

        public override void OnDestroy()
        {
            base.OnDestroy();

            isRunning = false;
            if (executorService != null)
            {
                executorService.ShutdownNow();
            }
            Cleanup();
            Log.Info(TAG, "Stopped");
        }

        private void Cleanup()
        {
            deviceToNetworkTCPQueue = null;
            deviceToNetworkUDPQueue = null;
            networkToDeviceQueue = null;
            ByteBufferPool.Clear();
            CloseResources(udpSelector, tcpSelector, vpnInterface);
        }

        // TODO: Move this to a "utils" class for reuse
        private static void CloseResources(params Java.IO.ICloseable[] resources)
        {
            foreach(var resource in resources)
            {
                try
                {
                    if (resource != null)
                    {
                        resource.Close();
                    }
                }
                catch (IOException e)
                {
                    // Ignore
                }
            }
        }

        private class VPNRunnable : Java.Lang.Object, IRunnable
        {
            private const string TAG = "VPNRunnable";

            private FileDescriptor vpnFileDescriptor;

            private ConcurrentLinkedQueue deviceToNetworkUDPQueue;
            private ConcurrentLinkedQueue deviceToNetworkTCPQueue;
            private ConcurrentLinkedQueue networkToDeviceQueue;

            public VPNRunnable(FileDescriptor vpnFileDescriptor,
                               ConcurrentLinkedQueue deviceToNetworkUDPQueue,
                               ConcurrentLinkedQueue deviceToNetworkTCPQueue,
                               ConcurrentLinkedQueue networkToDeviceQueue)
            {
                this.vpnFileDescriptor = vpnFileDescriptor;
                this.deviceToNetworkUDPQueue = deviceToNetworkUDPQueue;
                this.deviceToNetworkTCPQueue = deviceToNetworkTCPQueue;
                this.networkToDeviceQueue = networkToDeviceQueue;
            }

            public void Run()
            {
                Log.Info(TAG, "Started");

                FileChannel vpnInput = new FileInputStream(vpnFileDescriptor).Channel;
                FileChannel vpnOutput = new FileOutputStream(vpnFileDescriptor).Channel;

                try
                {
                    ByteBuffer bufferToNetwork = null;
                    bool dataSent = true;
                    bool dataReceived;
                    while (!Thread.Interrupted())
                    {
                        if (dataSent)
                            bufferToNetwork = ByteBufferPool.acquire();
                        else
                            bufferToNetwork.Clear();

                        // TODO: Block when not connected
                        int readBytes = vpnInput.Read(bufferToNetwork);
                        if (readBytes > 0)
                        {
                            dataSent = true;
                            bufferToNetwork.Flip();
                            Packet packet = new Packet(bufferToNetwork);
                            if (packet.IsUDP)
                            {
                                deviceToNetworkUDPQueue.Offer(packet);
                            }
                            else if (packet.IsTCP)
                            {
                                deviceToNetworkTCPQueue.Offer(packet);
                            }
                            else
                            {
                                Log.Warn(TAG, "Unknown packet type");
                                Log.Warn(TAG, packet.ip4Header.ToString());
                                dataSent = false;
                            }
                        }
                        else
                        {
                            dataSent = false;
                        }

                        ByteBuffer bufferFromNetwork = (ByteBuffer)networkToDeviceQueue.Poll();
                        if (bufferFromNetwork != null)
                        {
                            bufferFromNetwork.Flip();
                            while (bufferFromNetwork.HasRemaining)
                                vpnOutput.Write(bufferFromNetwork);
                            dataReceived = true;

                            ByteBufferPool.Release(bufferFromNetwork);
                        }
                        else
                        {
                            dataReceived = false;
                        }

                        // TODO: Sleep-looping is not very battery-friendly, consider blocking instead
                        // Confirm if throughput with ConcurrentQueue is really higher compared to BlockingQueue
                        if (!dataSent && !dataReceived)
                            Thread.Sleep(10);
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
                finally
                {
                    CloseResources(vpnInput, vpnOutput);
                }
            }
        }
    }
}
