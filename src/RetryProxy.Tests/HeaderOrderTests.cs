using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RetryProxy.Core.Tls;
using Xunit;

namespace RetryProxy.Tests;

/// <summary>指纹连接上的请求头重排：按顺序头重排、分帧后透传正文、不把内部顺序头发给上游。</summary>
public class HeaderOrderTests
{
    private const string Plan = HeaderOrderStream.PlanHeader;

    [Fact]
    public void ReordersHeadersByPlanAndRestoresNameCase()
    {
        var head = "POST /v1/messages?beta=true HTTP/1.1\r\n"
                   + "Host: api.example.com\r\n"
                   + "Accept: application/json\r\n"
                   + "User-Agent: claude-cli/2.1.283\r\n"
                   + "Authorization: Bearer sk-test\r\n"
                   + "x-app: cli\r\n"
                   + "Accept-Encoding: gzip, deflate, br, zstd\r\n"
                   + "X-Added-By-Proxy: 1\r\n"
                   + $"{Plan}: Accept,Authorization,content-type,User-Agent,x-app,Connection,Host,Accept-Encoding,Content-Length\r\n"
                   + "Content-Type: application/json\r\n"
                   + "Content-Length: 2\r\n"
                   + "\r\n{}";

        var output = Run(new RequestHeadReorderer(), head);

        Assert.Equal("POST /v1/messages?beta=true HTTP/1.1\r\n"
                     + "Accept: application/json\r\n"
                     + "Authorization: Bearer sk-test\r\n"
                     + "content-type: application/json\r\n"
                     + "User-Agent: claude-cli/2.1.283\r\n"
                     + "x-app: cli\r\n"
                     + "Host: api.example.com\r\n"
                     + "Accept-Encoding: gzip, deflate, br, zstd\r\n"
                     + "Content-Length: 2\r\n"
                     + "X-Added-By-Proxy: 1\r\n"
                     + "\r\n{}", output);
    }

    [Fact]
    public void LeavesHeadUntouchedWithoutPlan()
    {
        const string request = "HEAD /api/hello HTTP/1.1\r\nHost: api.example.com\r\nUser-Agent: Bun/1.4.3\r\nAccept: */*\r\n\r\n";
        Assert.Equal(request, Run(new RequestHeadReorderer(), request));
    }

    [Fact]
    public void KeepsFramingAcrossRequestsAndArbitraryWriteBoundaries()
    {
        // 正文里故意放一段像请求头的文本：按 Content-Length 计数透传，不能被当成下一个请求头。
        var body = "{\"text\":\"GET / HTTP/1.1\\r\\nx-retry-header-order: Host\\r\\n\\r\\n\"}";
        var first = $"POST /v1/messages HTTP/1.1\r\nHost: h\r\nx-b: 2\r\nx-a: 1\r\n{Plan}: x-a,x-b,Host,Content-Length\r\nContent-Length: {Encoding.Latin1.GetByteCount(body)}\r\n\r\n{body}";
        var second = $"HEAD /api/hello HTTP/1.1\r\nHost: h\r\nx-b: 2\r\nx-a: 1\r\n{Plan}: x-a,Host,x-b\r\n\r\n";
        var expected = $"POST /v1/messages HTTP/1.1\r\nx-a: 1\r\nx-b: 2\r\nHost: h\r\nContent-Length: {Encoding.Latin1.GetByteCount(body)}\r\n\r\n{body}"
                       + "HEAD /api/hello HTTP/1.1\r\nx-a: 1\r\nHost: h\r\nx-b: 2\r\n\r\n";
        var input = Encoding.Latin1.GetBytes(first + second);

        Assert.Equal(expected, Run(new RequestHeadReorderer(), first + second));
        foreach (var size in new[] { 1, 2, 3, 7, 64 })
        {
            var reorderer = new RequestHeadReorderer();
            var output = new MemoryStream();
            for (var offset = 0; offset < input.Length; offset += size)
            {
                foreach (var segment in reorderer.Process(input.AsMemory(offset, Math.Min(size, input.Length - offset))))
                {
                    output.Write(segment.Span);
                }
            }

            Assert.Equal(expected, Encoding.Latin1.GetString(output.ToArray()));
        }
    }

    [Fact]
    public void FollowsChunkedBodiesToTheNextRequest()
    {
        var chunked = $"POST /upload HTTP/1.1\r\nHost: h\r\nTransfer-Encoding: chunked\r\nx-a: 1\r\n{Plan}: x-a,Host\r\n\r\n"
                      + "5;ext=1\r\nhello\r\n1a\r\nabcdefghijklmnopqrstuvwxyz\r\n0\r\nx-trailer: t\r\n\r\n";
        var next = $"GET /next HTTP/1.1\r\nHost: h\r\nx-a: 1\r\n{Plan}: x-a,Host\r\n\r\n";

        var output = Run(new RequestHeadReorderer(), chunked + next);

        Assert.Equal("POST /upload HTTP/1.1\r\nx-a: 1\r\nHost: h\r\nTransfer-Encoding: chunked\r\n\r\n"
                     + "5;ext=1\r\nhello\r\n1a\r\nabcdefghijklmnopqrstuvwxyz\r\n0\r\nx-trailer: t\r\n\r\n"
                     + "GET /next HTTP/1.1\r\nx-a: 1\r\nHost: h\r\n\r\n", output);
    }

    [Theory]
    [InlineData("POST / HTTP/1.1\r\nHost: h\r\nContent-Length: abc\r\n\r\n")]
    [InlineData("POST / HTTP/1.1\r\nHost: h\r\nbroken line\r\n\r\n")]
    [InlineData("POST / HTTP/1.1\r\nHost: h\r\nTransfer-Encoding: chunked\r\n\r\nzz\r\n")]
    public void FailsInsteadOfGuessingWhenFramingIsUnknown(string request)
    {
        Assert.Throws<IOException>(() => Run(new RequestHeadReorderer(), request));
    }

    [Fact]
    public void SmallHeadAndBodyAreSentAsOneWrite()
    {
        var request = $"POST / HTTP/1.1\r\nHost: h\r\n{Plan}: Host,Content-Length\r\nContent-Length: 2\r\n\r\n{{}}";
        Assert.Single(new RequestHeadReorderer().Process(Encoding.Latin1.GetBytes(request)));
    }

    private static string Run(RequestHeadReorderer reorderer, string input) =>
        Encoding.Latin1.GetString(reorderer.Process(Encoding.Latin1.GetBytes(input)).SelectMany(segment => segment.ToArray()).ToArray());
}
