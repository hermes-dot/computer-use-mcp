// TCP 传输：当宿主禁止子进程管道 stdio 时的回退通道。
//
// 背景：某些沙箱（例如带管道限制的执行环境）会拒绝对子进程使用 stdio 管道，
// 此时 NDJSON over stdio 无法工作。回退方案是让 helper 监听 127.0.0.1 上的一个端口，
// 协议与 stdio 完全一致（一行一个 JSON），只是换了个字节通道。
//
// 安全约束：
//   - 只绑定回环地址，不暴露到网络；
//   - 必须携带启动时随机生成的 token，防止同机其他进程误调用（这是能控制鼠标键盘的接口）。
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CuHelper
{
    internal static class TcpTransport
    {
        /// <summary>
        /// 用法：cu-helper --tcp [--port N] --port-file &lt;path&gt; --token &lt;secret&gt;
        /// port 为 0 时由系统分配，实际端口写入 port-file，父进程轮询该文件即可。
        /// </summary>
        public static int Run(string[] args, Encoding utf8)
        {
            int port = 0;
            string portFile = null;
            string token = null;

            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--port":
                        if (i + 1 < args.Length) int.TryParse(args[++i], out port);
                        break;
                    case "--port-file":
                        if (i + 1 < args.Length) portFile = args[++i];
                        break;
                    case "--token":
                        if (i + 1 < args.Length) token = args[++i];
                        break;
                }
            }

            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            int actualPort = ((IPEndPoint)listener.LocalEndpoint).Port;

            if (!string.IsNullOrEmpty(portFile))
            {
                try
                {
                    File.WriteAllText(portFile, actualPort.ToString(System.Globalization.CultureInfo.InvariantCulture), utf8);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("cannot write port file: " + ex.Message);
                    return 2;
                }
            }

            // 只服务第一个连接：MCP server 全程复用同一条连接。
            using (var client = listener.AcceptTcpClient())
            {
                listener.Stop();
                client.NoDelay = true;
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, utf8))
                using (var writer = new StreamWriter(stream, utf8) { AutoFlush = true })
                {
                    // 首行必须是 token，握手失败立即断开。
                    string hello = reader.ReadLine();
                    if (!string.Equals(hello, token, StringComparison.Ordinal))
                    {
                        writer.WriteLine("{\"id\":0,\"ok\":false,\"error\":{\"code\":\"unauthorized\",\"message\":\"invalid token\"}}");
                        return 3;
                    }
                    writer.WriteLine("{\"id\":0,\"ok\":true,\"result\":{\"hello\":true}}");
                    return Program.Serve(reader, writer);
                }
            }
        }
    }
}
