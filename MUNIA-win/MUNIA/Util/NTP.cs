using System;
using System.Net;
using System.Net.Sockets;

namespace MUNIA.Util
{
    public class NTP
    {
        // stackoverflow.com/a/12150289
        public static DateTime Now
        {
            get
            {
                const string ntpServer = "time.windows.com";

                var ntpData = new byte[48];

                ntpData[0] = 0x1B;

                var addresses = Dns.GetHostEntry(ntpServer).AddressList;

                var ipEndPoint = new IPEndPoint(addresses[0], 123);
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

                socket.Connect(ipEndPoint);

                socket.ReceiveTimeout = 1000;

                var before = TimeStamp.Now;

                socket.Send(ntpData);
                socket.Receive(ntpData);

                var delta = TimeSpan.FromTicks((TimeStamp.Now - before).Ticks / 2);

                socket.Close();

                const byte serverReplyTime = 40;

                long intPart = SwapEndianness(BitConverter.ToUInt32(ntpData, serverReplyTime));
                long fractPart = SwapEndianness(BitConverter.ToUInt32(ntpData, serverReplyTime + 4));

                var ticks = (intPart * 10000000L) + ((fractPart * 10000000L) / 0x100000000L);

                var networkDateTime = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(ticks);

                return networkDateTime + delta;
            }
        }

        // stackoverflow.com/a/3294698
        static uint SwapEndianness(ulong x)
        {
            return (uint)(((x & 0x000000ff) << 24) +
                           ((x & 0x0000ff00) << 8) +
                           ((x & 0x00ff0000) >> 8) +
                           ((x & 0xff000000) >> 24));
        }
    }
}
