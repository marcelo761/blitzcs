using System.Net;
using System.Net.Sockets;
using System.Text;
using Blitz3DCS.Runtime;
using BlitzCS.LegacyRuntime;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class LegacyTcpStreamTests
{
    [Fact]
    public async Task Tcp_stream_reports_available_bytes_and_partial_read_then_eof()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();
        var stream = LegacyStreams.OpenTCPStream("127.0.0.1", port);
        Assert.NotEqual(0, stream);
        var bank = LegacyBanks.CreateBank(4);
        try
        {
            using var peer = await accept;
            Assert.Equal(0, LegacyStreams.ReadAvail(stream));
            Assert.Equal(0, LegacyStreams.Eof(stream));

            await peer.GetStream().WriteAsync(new byte[] { 0x31, 0x32 });
            Assert.True(SpinWait.SpinUntil(() => LegacyStreams.ReadAvail(stream) == 2, TimeSpan.FromSeconds(2)));
            peer.Client.Shutdown(SocketShutdown.Send);

            Assert.Equal(2, LegacyStreams.ReadBytes(bank, stream, 0, 4));
            Assert.Equal(new byte[] { 0x31, 0x32, 0, 0 }, ReadBank(bank));
            Assert.Equal(1, LegacyStreams.Eof(stream));
            Assert.Equal(0, LegacyStreams.ReadAvail(stream));
        }
        finally
        {
            LegacyStreams.CloseTCPStream(stream);
            LegacyBanks.FreeBank(bank);
        }
    }

    [Fact]
    public async Task Tcp_stream_supports_line_and_bank_io_on_the_same_handle()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var exchange = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync();
            var socket = peer.GetStream();
            var line = new List<byte>();
            var single = new byte[1];
            while (true)
            {
                if (await socket.ReadAsync(single.AsMemory()) == 0 || single[0] == '\n') break;
                if (single[0] != '\r') line.Add(single[0]);
            }

            var payload = new byte[2];
            await socket.ReadExactlyAsync(payload);
            await socket.WriteAsync("PONG\r\n"u8.ToArray());
            await socket.WriteAsync(new byte[] { 0x41, 0x42 });
            peer.Client.Shutdown(SocketShutdown.Send);
            return (Encoding.ASCII.GetString(line.ToArray()), payload);
        });

        var stream = LegacyStreams.OpenTCPStream("127.0.0.1", port);
        Assert.NotEqual(0, stream);
        var bank = LegacyBanks.CreateBank(4);
        try
        {
            LegacyBanks.WithBytes(bank, 0, 4, bytes => { new byte[] { 10, 20, 30, 40 }.CopyTo(bytes); return 0; });
            LegacyStreams.WriteLine(stream, "PING");
            Assert.Equal(2, LegacyStreams.WriteBytes(bank, stream, 1, 2));

            Assert.Equal("PONG", LegacyStreams.ReadLine(stream));
            Assert.Equal(2, LegacyStreams.ReadBytes(bank, stream, 0, 2));
            var serverResult = await exchange.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("PING", serverResult.Item1);
            Assert.Equal(new byte[] { 20, 30 }, serverResult.Item2);
            Assert.Equal(new byte[] { 0x41, 0x42, 30, 40 }, ReadBank(bank));
        }
        finally
        {
            LegacyStreams.CloseTCPStream(stream);
            LegacyBanks.FreeBank(bank);
        }
    }

    private static byte[] ReadBank(int bank) => LegacyBanks.WithBytes(
        bank, 0, LegacyBanks.BankSize(bank), bytes => bytes.ToArray());
}
